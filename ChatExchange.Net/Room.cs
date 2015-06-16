/*
 * ChatExchange.Net. A .Net (4.0) API for interacting with Stack Exchange chat.
 * Copyright � 2015, ArcticEcho.
 *
 * This program is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 2 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 */





using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using CsQuery;
using ServiceStack.Text;
using WebSocketSharp;

namespace ChatExchangeDotNet
{
    public class Room : IDisposable
    {
        private bool disposed;
        private bool hasLeft;
        private string fkey;
        private TimeSpan socketRecTimeout;
        private WebSocket socket;
        private EventManager evMan;
        private readonly AutoResetEvent throttleARE;
        private readonly ActionExecutor actEx;
        private readonly string chatRoot;
        private readonly string cookieKey;

        # region Public properties/indexer.

        /// <summary>
        /// If true, actions by the currently logged in user will not raise any events. Default set to true.
        /// </summary>
        public bool IgnoreOwnEvents { get; set; }

        /// <summary>
        /// If true, removes (@Username) mentions and the message reply prefix (:012345) from all messages. Default set to true.
        /// </summary>
        public bool StripMentionFromMessages { get; set; }

        /// <summary>
        /// Specifies how long to attempt to recovery the WebSocket after the connection closed;
        /// after which, an error is passed to the InternalException event and the room self-destructs.
        /// (Default set to 15 minutes.)
        /// </summary>
        public TimeSpan WebSocketRecoveryTimeout
        {
            get { return socketRecTimeout; }

            set
            {
                if (value.TotalSeconds < 10) { throw new ArgumentOutOfRangeException("value", "Must be more then 10 seconds."); }

                socketRecTimeout = value;
            }
        }

        /// <summary>
        /// The host domain of the room.
        /// </summary>
        public string Host { get; private set; }

        /// <summary>
        /// The identification number of the room.
        /// </summary>
        public int ID { get; private set; }

        /// <summary>
        /// Returns the currently logged in user.
        /// </summary>
        public User Me { get; private set; }

        /// <summary>
        /// The EventManager object provides an interface to (dis)connect/update chat event listeners.
        /// </summary>
        public EventManager EventManager { get { return evMan; } }

        /// <summary>
        /// Gets the Message object associated with the specified message ID.
        /// </summary>
        /// <param name="messageID"></param>
        /// <returns>The Message object associated with the specified ID.</returns>
        public Message this[int messageID]
        {
            get
            {
                if (messageID < 0) { throw new IndexOutOfRangeException(); }

                return GetMessage(messageID);
            }
        }

        # endregion.



        /// <summary>
        /// Provides access to chat room functions, such as, message posting/editing/deleting/starring, user kick-muting/access level changing, basic message/user retrieval and the ability to subscribe to events.
        /// </summary>
        /// <param name="host">The host domain of the room (e.g., meta.stackexchange.com).</param>
        /// <param name="ID">The room's identification number.</param>
        public Room(string cookieKey, string host, int ID)
        {
            if (string.IsNullOrEmpty(cookieKey)) { throw new ArgumentNullException("cookieKey"); }
            if (string.IsNullOrEmpty(host)) { throw new ArgumentNullException("'host' must not be null or empty.", "host"); }
            if (ID < 0) { throw new ArgumentOutOfRangeException("ID", "'ID' must not be negative."); }

            this.ID = ID;
            this.cookieKey = cookieKey;
            evMan = new EventManager();
            actEx = new ActionExecutor(ref evMan);
            chatRoot = "http://chat." + host;
            throttleARE = new AutoResetEvent(false);
            socketRecTimeout = TimeSpan.FromMinutes(15);
            Host = host;
            IgnoreOwnEvents = true;
            StripMentionFromMessages = true;
            Me = GetMe();

            SetFkey();

            var count = GetGlobalEventCount();
            var url = GetSocketURL(count);

            InitialiseSocket(url);
        }

        ~Room()
        {
            Dispose();
        }



        public void Dispose()
        {
            if (disposed) { return; }

            disposed = true;

            if (socket != null && socket.ReadyState == WebSocketState.Open)
            {
                try
                {
                    socket.Close(CloseStatusCode.Normal);
                }
                catch (Exception ex)
                {
                    evMan.CallListeners(EventType.InternalException, ex);
                }
            }

            if (throttleARE != null)
            {
                throttleARE.Set(); // Release any threads currently being throttled.
                throttleARE.Dispose();
            }
            if (actEx != null)
            {
                actEx.Dispose();
            }
            if (evMan != null)
            {
                evMan.Dispose();
            }
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Leave the room.
        /// </summary>
        public void Leave()
        {
            if (hasLeft) { return; }

            RequestManager.SendPOSTRequest(cookieKey, chatRoot + "/chats/leave/" + ID, "quiet=true&fkey=" + fkey);

            hasLeft = true;
        }

        /// <summary>
        /// Refreshes the 'Me' property with fresh data.
        /// </summary>
        public void RefreshMe()
        {
            Me = GetUser(Me.ID);
        }

        /// <summary>
        /// Retrieves a message from the room.
        /// </summary>
        /// <param name="messageID">The ID of the message to fetch.</param>
        /// <returns>A Message object representing the requested message, or null if the message could not be found.</returns>
        public Message GetMessage(int messageID)
        {
            var resContent = RequestManager.SendGETRequest(cookieKey, chatRoot + "/messages/" + messageID + "/history");

            if (string.IsNullOrEmpty(resContent))
            {
                throw new Exception("Could not retrieve data of message " + messageID + ". Do you have an active internet connection?");
            }

            var lastestDom = CQ.Create(resContent).Select(".monologue").Last();
            var content = Message.GetMessageContent(Host, messageID);

            if (content == null) { throw new Exception("The requested message was not found."); }

            var parentID = content.IsReply() ? int.Parse(content.Substring(1, content.IndexOf(' '))) : -1;
            var authorName = lastestDom[".username a"].First().Text();
            var authorID = int.Parse(lastestDom[".username a"].First().Attr("href").Split('/')[2]);

            return new Message(ref evMan, Host, ID, messageID, GetUser(authorID), StripMentionFromMessages, parentID);
        }

        /// <summary>
        /// Fetches user data for the specified user ID.
        /// </summary>
        /// <param name="userID">The user ID to look up.</param>
        public User GetUser(int userID)
        {
            return new User(ref evMan, Host, ID, userID);
        }

        /// <summary>
        /// Fetches a list of all users that are currently able to receive "ping"s.
        /// </summary>
        public HashSet<User> GetPingableUsers()
        {
            var json = RequestManager.SendGETRequest(cookieKey, "http://chat." + Host + "/rooms/pingable/" + ID);
            if (string.IsNullOrEmpty(json)) { return null; }
            var data = JsonSerializer.DeserializeFromString<HashSet<List<object>>>(json);
            var users = new HashSet<User>();

            foreach (var user in data)
            {
                var userID = int.Parse(user[0].ToString());
                users.Add(new User(Host, ID, userID, true));
            }

            return users;
        }

        # region Normal user chat commands.

        /// <summary>
        /// Posts a new message in the room.
        /// </summary>
        /// <param name="message">The message to post.</param>
        /// <returns>A Message object representing the newly posted message (if successful), otherwise returns null.</returns>
        public Message PostMessage(string message)
        {
            if (hasLeft) { return null; }

            var action = new ChatAction(ActionType.PostMessage, new Func<object>(() =>
            {
                while (!disposed)
                {
                    var data = "text=" + Uri.EscapeDataString(message).Replace("%5Cn", "%0A") + "&fkey=" + fkey;
                    var resContent = RequestManager.SendPOSTRequest(cookieKey, chatRoot + "/chats/" + ID + "/messages/new", data);

                    if (string.IsNullOrEmpty(resContent) || hasLeft) { return null; }
                    if (HandleThrottling(resContent)) { continue; }

                    var json = JsonObject.Parse(resContent);
                    var messageID = -1;
                    if (json.ContainsKey("id"))
                    {
                        messageID = json.Get<int>("id");
                    }
                    else
                    {
                        return null;
                    }

                    return new Message(ref evMan, Host, ID, messageID, Me, StripMentionFromMessages, -1);
                }

                return null;
            }));

            return (Message)actEx.ExecuteAction(action);
        }

        public Message PostReply(int targetMessageID, string message)
        {
            return PostMessage(":" + targetMessageID + " " + message);
        }

        public Message PostReply(Message targetMessage, string message)
        {
            return PostMessage(":" + targetMessage.ID + " " + message);
        }

        public bool EditMessage(Message oldMessage, string newMessage)
        {
            return EditMessage(oldMessage.ID, newMessage);
        }

        public bool EditMessage(int messageID, string newMessage)
        {
            if (hasLeft) { return false; }

            var action = new ChatAction(ActionType.EditMessage, new Func<object>(() =>
            {
                while (!disposed)
                {
                    var data = "text=" + Uri.EscapeDataString(newMessage).Replace("%5Cn", "%0A") + "&fkey=" + fkey;
                    var resContent = RequestManager.SendPOSTRequest(cookieKey, chatRoot + "/messages/" + messageID, data);

                    if (string.IsNullOrEmpty(resContent) || hasLeft) { return false; }
                    if (HandleThrottling(resContent)) { continue; }

                    return resContent == "\"ok\"";
                }

                return false;
            }));

            return (bool?)actEx.ExecuteAction(action) ?? false;
        }

        public bool DeleteMessage(Message message)
        {
            return DeleteMessage(message.ID);
        }

        public bool DeleteMessage(int messageID)
        {
            if (hasLeft) { return false; }

            var action = new ChatAction(ActionType.DeleteMessage, new Func<object>(() =>
            {
                while (!disposed)
                {
                    var resContent = RequestManager.SendPOSTRequest(cookieKey, chatRoot + "/messages/" + messageID + "/delete", "fkey=" + fkey);

                    if (string.IsNullOrEmpty(resContent) || hasLeft) { return false; }
                    if (HandleThrottling(resContent)) { continue; }

                    return resContent == "\"ok\"";
                }

                return false;
            }));

            return (bool?)actEx.ExecuteAction(action) ?? false;
        }

        public bool ToggleStar(Message message)
        {
            return ToggleStar(message.ID);
        }

        public bool ToggleStar(int messageID)
        {
            if (hasLeft) { return false; }

            var action = new ChatAction(ActionType.ToggleMessageStar, new Func<object>(() =>
            {
                while (!disposed)
                {
                    var resContent = RequestManager.SendPOSTRequest(cookieKey, chatRoot + "/messages/" + messageID + "/star", "fkey=" + fkey);

                    if (string.IsNullOrEmpty(resContent) || hasLeft) { return false; }
                    if (HandleThrottling(resContent)) { continue; }

                    return resContent != "\"ok\"";
                }

                return false;
            }));

            return (bool?)actEx.ExecuteAction(action) ?? false;
        }

        # endregion

        #region Owner chat commands.

        public bool ClearMessageStars(Message message)
        {
            return ClearMessageStars(message.ID);
        }

        public bool ClearMessageStars(int messageID)
        {
            var action = new ChatAction(ActionType.ClearMessageStars, new Func<object>(() =>
            {
                while (true)
                {
                    if (!Me.IsMod || !Me.IsRoomOwner || hasLeft) { return false; }

                    var data = "fkey=" + fkey;
                    var resContent = RequestManager.SendPOSTRequest(cookieKey, chatRoot + "/messages/" + messageID + "/unstar", data);

                    if (string.IsNullOrEmpty(resContent)) { return false; }
                    if (HandleThrottling(resContent)) { continue; }

                    return resContent == "\"ok\"";
                }
            }));

            return (bool?)actEx.ExecuteAction(action) ?? false;
        }

        public bool TogglePin(Message message)
        {
            return TogglePin(message.ID);
        }

        public bool TogglePin(int messageID)
        {
            var action = new ChatAction(ActionType.ToggleMessagePin, new Func<object>(() =>
            {
                while (true)
                {
                    if (!Me.IsMod || !Me.IsRoomOwner || hasLeft) { return false; }

                    var data = "fkey=" + fkey;
                    var resContent = RequestManager.SendPOSTRequest(cookieKey, chatRoot + "/messages/" + messageID + "/owner-star", data);

                    if (string.IsNullOrEmpty(resContent)) { return false; }
                    if (HandleThrottling(resContent)) { continue; }

                    return resContent == "\"ok\"";
                }
            }));

            return (bool?)actEx.ExecuteAction(action) ?? false;
        }

        public bool KickMute(User user)
        {
            return KickMute(user.ID);
        }

        public bool KickMute(int userID)
        {
            var action = new ChatAction(ActionType.KickMute, new Func<object>(() =>
            {
                while (true)
                {
                    if (!Me.IsMod || !Me.IsRoomOwner || hasLeft) { return false; }

                    var data = "userID=" + userID + "&fkey=" + fkey;
                    var resContent = RequestManager.SendPOSTRequest(cookieKey, chatRoot + "/rooms/kickmute/" + ID, data);

                    if (string.IsNullOrEmpty(resContent)) { return false; }
                    if (HandleThrottling(resContent)) { continue; }

                    return resContent != null && resContent.Contains("has been kicked");
                }
            }));

            return (bool?)actEx.ExecuteAction(action) ?? false;
        }

        public bool SetUserRoomAccess(UserRoomAccess access, User user)
        {
            return SetUserRoomAccess(access, user.ID);
        }

        public bool SetUserRoomAccess(UserRoomAccess access, int userID)
        {
            var action = new ChatAction(ActionType.KickMute, new Func<object>(() =>
            {
                while (true)
                {
                    if (!Me.IsMod || !Me.IsRoomOwner || hasLeft) { return false; }

                    var data = "fkey=" + fkey + "&aclUserId=" + userID + "&userAccess=";

                    switch (access)
                    {
                        case UserRoomAccess.Normal:
                        {
                            data += "remove";
                            break;
                        }

                        case UserRoomAccess.ExplicitReadOnly:
                        {
                            data += "read-only";
                            break;
                        }

                        case UserRoomAccess.ExplicitReadWrite:
                        {
                            data += "read-write";
                            break;
                        }

                        case UserRoomAccess.Owner:
                        {
                            data += "owner";
                            break;
                        }
                    }

                    var resContent = RequestManager.SendPOSTRequest(cookieKey, chatRoot + "/rooms/setuseraccess/" + ID, data);

                    if (string.IsNullOrEmpty(resContent)) { return false; }
                    if (HandleThrottling(resContent)) { continue; }

                    return true;
                }
            }));

            return (bool?)actEx.ExecuteAction(action) ?? false;
        }

        #endregion



        private bool HandleThrottling(string res)
        {
            if (Regex.IsMatch(res, @"(?i)^you can perform this action again in \d*") && !disposed)
            {
                var delay = Regex.Replace(res, @"\D", "");
                throttleARE.WaitOne(int.Parse(delay) * 1000);

                return true;
            }

            return false;
        }

        # region Instantiation related methods.

        private User GetMe()
        {
            var html = RequestManager.SendGETRequest(cookieKey, chatRoot + "/chats/join/favorite");

            if (string.IsNullOrEmpty(html)) { throw new Exception("Could not get user information. Do you have an active internet connection?"); }

            var dom = CQ.Create(html);
            var e = dom[".topbar-menu-links a"][0];
            var id = int.Parse(e.Attributes["href"].Split('/')[2]);

            return new User(Host, ID, id);
        }

        private void SetFkey()
        {
            var resContent = RequestManager.SendGETRequest(cookieKey, chatRoot + "/rooms/" + ID);
            var ex = new Exception("Could not get fkey. Do you have an active internet connection?");

            if (string.IsNullOrEmpty(resContent)) { throw ex; }

            var fk = CQ.Create(resContent).GetInputValue("fkey");

            if (string.IsNullOrEmpty(fk)) { throw ex; }

            fkey = fk;
        }

        private int GetGlobalEventCount()
        {
            var data = "mode=Events&msgCount=0&fkey=" + fkey;
            var resContent = RequestManager.SendPOSTRequest(cookieKey, chatRoot + "/chats/" + ID + "/events", data);

            if (string.IsNullOrEmpty(resContent))
            {
                throw new Exception("Could not get 'eventtime' for room " + ID + " on " + Host + ". Do you have an active internet conection?");
            }

            return JsonObject.Parse(resContent).Get<int>("time");
        }

        private string GetSocketURL(int eventTime)
        {
            var data = "roomid=" + ID + "&fkey=" + fkey;
            var resContent = RequestManager.SendPOSTRequest(cookieKey, chatRoot + "/ws-auth", data, chatRoot + "/rooms/" + ID, chatRoot);

            if (string.IsNullOrEmpty(resContent)) { throw new Exception("Could not get WebSocket URL. Do you haven an active internet connection?"); }

            return JsonObject.Parse(resContent).Get<string>("url") + "?l=" + eventTime;
        }

        private void InitialiseSocket(string socketUrl)
        {
            socket = new WebSocket(socketUrl) { Origin = chatRoot };

            socket.OnMessage += (o, oo) =>
            {
                try
                {
                    HandleData(oo.Data);
                }
                catch (Exception ex)
                {
                    evMan.CallListeners(EventType.InternalException, ex);
                }
            };

            socket.OnError += (o, oo) => evMan.CallListeners(EventType.InternalException, oo.Exception);

            socket.OnClose += (o, oo) =>
            {
                if (!oo.WasClean || oo.Code != (ushort)CloseStatusCode.Normal)
                {
                    // The socket closed abnormally, probably best to restart it.
                    for (var i = 0; i < socketRecTimeout.TotalMinutes * 6; i++)
                    {
                        Thread.Sleep(10000);

                        try
                        {
                            SetFkey();
                            var count = GetGlobalEventCount();
                            var url = GetSocketURL(count);
                            InitialiseSocket(url);
                            return;
                        }
                        catch (Exception ex)
                        {
                            evMan.CallListeners(EventType.InternalException, ex);
                        }
                    }

                    // We failed to restart the socket; dispose of the object and log the error.
                    evMan.CallListeners(EventType.InternalException, new Exception("Could not restart WebSocket; now disposing this Room object."));
                    Dispose();
                }
            };

            socket.Connect();
        }

        # endregion

        # region Incoming message handling methods.

        private void HandleData(string json)
        {
            var obj = JsonObject.Parse(json);
            var data = obj.Get<Dictionary<string, List<Dictionary<string, object>>>>("r" + ID);

            if (!data.ContainsKey("e") || data["e"] == null) { return; }

            foreach (var message in data["e"])
            {
                var eventType = (EventType)int.Parse(message["event_type"].ToString());
                if (int.Parse(message["room_id"].ToString()) != ID) { continue; }

                evMan.CallListeners(EventType.DataReceived, message.ToString());

                switch (eventType)
                {
                    case EventType.MessagePosted:
                    {
                        HandleNewMessage(message);
                        continue;
                    }
                    case EventType.MessageEdited:
                    {
                        HandleMessageEdit(message);
                        continue;
                    }
                    case EventType.UserEntered:
                    {
                        HandleUserJoinLeave(message, EventType.UserEntered);
                        continue;
                    }
                    case EventType.UserLeft:
                    {
                        HandleUserJoinLeave(message, EventType.UserLeft);
                        continue;
                    }
                    case EventType.MessageStarToggled:
                    {
                        HandleStarToggle(message);
                        continue;
                    }
                    case EventType.UserMentioned:
                    {
                        HandleUserMentioned(message);
                        continue;
                    }
                    case EventType.UserAccessLevelChanged:
                    {
                        HandleUserAccessChange(message);
                        continue;
                    }
                    case EventType.MessageReply:
                    {
                        HandleMessageReply(message);
                        continue;
                    }
                }
            }
        }

        private void HandleNewMessage(Dictionary<string, object> data)
        {
            var authorID = int.Parse(data["user_id"].ToString());

            if (authorID == Me.ID && IgnoreOwnEvents) { return; }

            var id = int.Parse(data["message_id"].ToString());
            var parentID = -1;
            if (data.ContainsKey("parent_id") && data["parent_id"] != null)
            {
                parentID = int.Parse(data["parent_id"].ToString());
            }

            var message = new Message(ref evMan, Host, ID, id, GetUser(authorID), StripMentionFromMessages, parentID);

            evMan.CallListeners(EventType.MessagePosted, message);
        }

        private void HandleMessageEdit(Dictionary<string, object> data)
        {
            var authorID = int.Parse(data["user_id"].ToString());

            if (authorID == Me.ID && IgnoreOwnEvents) { return; }

            var id = int.Parse(data["message_id"].ToString());
            var parentID = -1;
            if (data.ContainsKey("parent_id") && data["parent_id"] != null)
            {
                parentID = int.Parse(data["parent_id"].ToString());
            }

            var currentMessage = new Message(ref evMan, Host, ID, id, GetUser(authorID), StripMentionFromMessages, parentID);

            evMan.CallListeners(EventType.MessageEdited, currentMessage);
        }

        private void HandleUserJoinLeave(Dictionary<string, object> data, EventType type)
        {
            var userID = int.Parse(data["user_id"].ToString());

            if (userID == Me.ID && IgnoreOwnEvents) { return; }

            evMan.CallListeners(type, GetUser(userID));
        }

        private void HandleStarToggle(Dictionary<string, object> data)
        {
            var starrerID = int.Parse(data["user_id"].ToString());

            if (starrerID == Me.ID && IgnoreOwnEvents) { return; }

            var id = int.Parse(data["message_id"].ToString());
            var starCount = 0;
            var pinCount = 0;

            if (data.ContainsKey("message_stars") && data["message_stars"] != null)
            {
                starCount = int.Parse(data["message_stars"].ToString());
            }

            if (data.ContainsKey("message_owner_stars") && data["message_owner_stars"] != null)
            {
                pinCount = int.Parse(data["message_owner_stars"].ToString());
            }

            var message = this[id];
            var user = new User(Host, ID, starrerID);

            evMan.CallListeners(EventType.MessageStarToggled, message, user, starCount, pinCount);
        }

        private void HandleUserMentioned(Dictionary<string, object> data)
        {
            var authorID = int.Parse(data["user_id"].ToString());

            if (authorID == Me.ID && IgnoreOwnEvents) { return; }

            var id = int.Parse(data["message_id"].ToString());
            var parentID = -1;
            if (data.ContainsKey("parent_id") && data["parent_id"] != null)
            {
                parentID = int.Parse(data["parent_id"].ToString());
            }

            var message = new Message(ref evMan, Host, ID, id, GetUser(authorID), StripMentionFromMessages, parentID);

            evMan.CallListeners(EventType.UserMentioned, message);
        }

        private void HandleUserAccessChange(Dictionary<string, object> data)
        {
            var granterID = int.Parse(data["user_id"].ToString());

            if (granterID == Me.ID && IgnoreOwnEvents) { return; }

            var targetUserID = int.Parse(data["target_user_id"].ToString());
            var content = (string)data["content"];
            var granter = GetUser(granterID);
            var targetUser = GetUser(targetUserID);
            var newAccessLevel = UserRoomAccess.Normal;

            switch (content)
            {
                case "Access now owner":
                {
                    newAccessLevel = UserRoomAccess.Owner;
                    break;
                }
                case "Access now read-write":
                {
                    newAccessLevel = UserRoomAccess.ExplicitReadWrite;
                    break;
                }
                case "Access now read-only":
                {
                    newAccessLevel = UserRoomAccess.ExplicitReadOnly;
                    break;
                }
            }

            evMan.CallListeners(EventType.UserAccessLevelChanged, granter, targetUser, newAccessLevel);
        }

        private void HandleMessageReply(Dictionary<string, object> data)
        {
            var authorID = int.Parse(data["user_id"].ToString());

            if (authorID == Me.ID && IgnoreOwnEvents) { return; }

            var id = int.Parse(data["message_id"].ToString());
            var parentID = int.Parse(data["parent_id"].ToString());
            var parent = this[parentID];
            var message = new Message(ref evMan, Host, ID, id, GetUser(authorID), StripMentionFromMessages, parentID);

            evMan.CallListeners(EventType.MessageReply, parent, message);
        }

        # endregion
    }
}
