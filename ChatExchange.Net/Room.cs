﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using CsQuery;
using Newtonsoft.Json.Linq;
using WebSocket4Net;



namespace ChatExchangeDotNet
{
	public class Room : IDisposable
	{
		private readonly Regex messageHasParent = new Regex(@"^:\d*?\s", RegexOptions.Compiled | RegexOptions.CultureInvariant);
		private bool disposed;
		private WebSocket socket;
		private readonly string chatRoot;
		private string fkey;

		# region Events.

		/// <param name="newMessage">The newly posted message.</param>
		public delegate void NewMessageEventHandler(Message newMessage);

		/// <param name="oldMessage">The previous state of the message.</param>
		/// <param name="newMessage">The current state of the message.</param>
		public delegate void MessageEditedEventHandler(Message oldMessage, Message newMessage);

		/// <param name="message">The message that someone has (un)starred.</param>
		/// <param name="starer">The user that (un)starred the message.</param>
		/// <param name="starCount">The current star count.</param>
		/// <param name="starCount">The current pin count.</param>
		public delegate void MessageStarToggledEventHandler(Message message, User starer, int starCount, int pinCount);

		/// <param name="user">The user that has joined/entered the room.</param>
		public delegate void UserJoinEventHandler(User user);

		/// <param name="user">The user that has left the room.</param>
		public delegate void UserLeftEventHandler(User user);

		/// <summary>
		/// Occurs when a new message is posted. Returns the newly posted message.
		/// </summary>
		public event NewMessageEventHandler NewMessage;

		/// <summary>
		/// Occurs when a message is edited.
		/// </summary>
		public event MessageEditedEventHandler MessageEdited;

		/// <summary>
		/// Occurs when someone stars/pins (or unstars/unpins) a message.
		/// </summary>
		public event MessageStarToggledEventHandler MessageStarToggled;

		/// <summary>
		/// Occurs when a user joins/enters the room.
		/// </summary>
		public event UserJoinEventHandler UserJoind;

		/// <summary>
		/// Occurs when a user leaves the room.
		/// </summary>
		public event UserLeftEventHandler UserLeft;

		# endregion.

		# region Public properties/indexer.

		/// <summary>
		/// If true, all events triggered by the loggined user will not be raised. Default set to true.
		/// </summary>
		public bool IgnoreOwnEvents { get; set; }

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
		/// Messages posted by all users from when this object was first instantiated.
		/// </summary>
		public List<Message> AllMessages { get; private set; }

		/// <summary>
		/// All successfully posted messages by the currently logged in user.
		/// </summary>
		public List<Message> MyMessages { get; private set; }

		/// <summary>
		/// A list of all the "pingable" users in the room.
		/// </summary>
		public List<User> PingableUsers { get; private set; }

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

				if (AllMessages.Any(m => m.ID == messageID))
				{
					return AllMessages.First(m => m.ID == messageID);
				}

				var message = GetMessage(messageID);

				AllMessages.Add(message);

				return message;		
			}
		}

		# endregion.



		/// <summary>
		/// WARNING! This class is not yet fully implemented/tested!
		/// </summary>
		/// <param name="host">The host domain of the room (e.g., meta.stackexchange.com).</param>
		/// <param name="ID">The room's identification number.</param>
		/// <param name="parsingOption">The MessageParsingOption to be used when parsing received messages.</param>
		public Room(string host, int ID)
		{
			if (String.IsNullOrEmpty(host)) { throw new ArgumentException("'host' can not be null or empty.", "host"); }
			if (ID < 0) { throw new ArgumentOutOfRangeException("ID", "'ID' can not be negative."); }

			IgnoreOwnEvents = true;
			this.ID = ID;
			Host = host;
			PingableUsers = GetPingableUsers();
			AllMessages = new List<Message>();
			MyMessages = new List<Message>();
			chatRoot = "http://chat." + Host;
			Me = GetMe();

			SetFkey();

			var count = GetGlobalEventCount();

			var url = GetSocketURL(count);

			InitialiseSocket(url);
		}

		~Room()
		{
			if (socket != null && !disposed)
			{
				socket.Close();
			}
		}



		public Message GetMessage(int messageID)
		{
			var res = RequestManager.SendGETRequest(chatRoot + "/messages/" + messageID + "/history");

			if (res == null) { throw new Exception("Could not retrieve data of message " + messageID + ". Do you have an active internet connection?"); }

			var lastestDom = CQ.Create(RequestManager.GetResponseContent(res)).Select(".monologue").Last();
			
			var content = Message.GetMessageContent(Host, ID, messageID); 

			var parentID = messageHasParent.IsMatch(content) ? int.Parse(content.Substring(1, content.IndexOf(' '))) : -1;
			var authorName = lastestDom[".username a"].First().Text();
			var authorID = int.Parse(lastestDom[".username a"].First().Attr("href").Split('/')[2]);

			return new Message(Host, content, messageID, authorName, authorID, parentID);
		}

		# region Normal user chat commands.

		public Message PostMessage(string message)
		{
			var data = "text=" + Uri.EscapeDataString(message) + "&fkey=" + fkey;

			RequestManager.CookiesToPass = RequestManager.GlobalCookies;

			var res = RequestManager.SendPOSTRequest(chatRoot + "/chats/" + ID + "/messages/new", data);

			if (res == null) { return null; }

			var json = JObject.Parse(RequestManager.GetResponseContent(res));

			var messageID = (int)(json["id"].Type != JTokenType.Integer ? -1 : json["id"]);

			if (messageID == -1) { return null; }

			var m = new Message(Host, message, messageID, Me.Name, Me.ID);

			MyMessages.Add(m);
			AllMessages.Add(m);

			return m;
		}

		public bool EditMessage(Message oldMessage, string newMessage)
		{
			return EditMessage(oldMessage.ID, newMessage);
		}

		public bool EditMessage(int messageID, string newMessage)
		{
			var data = "text=" + Uri.EscapeDataString(newMessage) + "&fkey=" + fkey;

			var res = RequestManager.SendPOSTRequest(chatRoot + "/messages/" + messageID, data);

			return res != null && RequestManager.GetResponseContent(res) == "\"ok\"";
		}

		public bool DeleteMessage(Message message)
		{
			return DeleteMessage(message.ID);
		}

		public bool DeleteMessage(int messageID)
		{
			var data = "fkey=" + fkey;

			var res = RequestManager.SendPOSTRequest(chatRoot + "/messages/" + messageID + "/delete", data);

			return res != null && RequestManager.GetResponseContent(res) == "\"ok\"";
		}

		public bool ToggleStarring(Message message)
		{
			return ToggleStarring(message.ID);
		}

		public bool ToggleStarring(int messageID)
		{
			var data = "fkey=" + fkey;

			var res = RequestManager.SendPOSTRequest(chatRoot + "/messages/" + messageID + "/star", data);

			return res != null && RequestManager.GetResponseContent(res) != "\"ok\"";
		}

		# endregion

		#region Owner chat commands.

		/// <summary>
		/// WARNING! This method has not yet been fully tested!
		/// </summary>
		public bool UnstarMessage(Message message)
		{
			return UnstarMessage(message.ID);
		}

		/// <summary>
		/// WARNING! This method has not yet been fully tested!
		/// </summary>
		public bool UnstarMessage(int messageID)
		{
			if (!Me.IsMod && !Me.IsRoomOwner) { return false; }

			var data = "fkey=" + fkey;

			var res = RequestManager.SendPOSTRequest(chatRoot + "/messages/" + messageID + "/unstar", data);

			return res != null && RequestManager.GetResponseContent(res) != "\"ok\"";
		}

		/// <summary>
		/// WARNING! This method has not yet been fully tested!
		/// </summary>
		public bool TogglePinning(Message message)
		{
			return TogglePinning(message.ID);
		}
		
		/// <summary>
		/// WARNING! This method has not yet been fully tested!
		/// </summary>
		public bool TogglePinning(int messageID)
		{
			if (!Me.IsMod && !Me.IsRoomOwner) { return false; }

			var data = "fkey=" + fkey;

			var res = RequestManager.SendPOSTRequest(chatRoot + "/messages/" + messageID + "/owner-star", data);

			return res != null && RequestManager.GetResponseContent(res) != "\"ok\"";
		}

		/// <summary>
		/// WARNING! This method has not yet been fully tested!
		/// </summary>
		public bool KickMute(User user)
		{
			return KickMute(user.ID);
		}

		/// <summary>
		/// WARNING! This method has not yet been fully tested!
		/// </summary>
		public bool KickMute(int userID)
		{
			if (!Me.IsMod && !Me.IsRoomOwner) { return false; }

			var data = "userID=" + userID + "&fkey=" + fkey;

			var res = RequestManager.SendPOSTRequest(chatRoot + "/rooms/kickute/" + ID, data);

			return res != null && RequestManager.GetResponseContent(res).Contains("The user has been kicked and cannot return");
		}

		/// <summary>
		/// WARNING! This method has not yet been fully tested!
		/// </summary>
		public bool SetUserRoomAccess(UserRoomAccess access, User user)
		{
			return SetUserRoomAccess(access, user.ID);
		}

		/// <summary>
		/// WARNING! This method has not yet been fully tested!
		/// </summary>
		public bool SetUserRoomAccess(UserRoomAccess access, int userID)
		{
			if (!Me.IsMod && !Me.IsRoomOwner) { return false; }

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

			var res = RequestManager.SendPOSTRequest(chatRoot + "/rooms/setuseraccess/" + ID, data);

			return res != null;
		}

		#endregion

		#region Inherited methods.

		public void Dispose()
		{
			if (disposed) { return; }

			socket.Close();

			GC.SuppressFinalize(this);

			disposed = true;
		}

		public static bool operator ==(Room a, Room b)
		{
			if ((object)a == null || (object)b == null) { return false; }

			if (ReferenceEquals(a, b)) { return true; }

			return a.GetHashCode() == b.GetHashCode();
		}

		public static bool operator !=(Room a, Room b)
		{
			return !(a == b);
		}

		public bool Equals(Room room)
		{
			if (room == null) { return false; }

			return room.GetHashCode() == GetHashCode();
		}

		public bool Equals(string host, int id)
		{
			if (String.IsNullOrEmpty(host) || id < 0) { return false; }

			return String.Equals(host, Host, StringComparison.InvariantCultureIgnoreCase) && ID == id;
		}

		public override bool Equals(object obj)
		{
			if (obj == null) { return false; }

			if (!(obj is Room)) { return false; }

			return obj.GetHashCode() == GetHashCode();
		}

		public override int GetHashCode()
		{
			return Host.GetHashCode() + ID.GetHashCode();
		}

		#endregion



		# region Instantiation related methods.

		private List<User> GetPingableUsers()
		{
			var users = new List<User>();



			return users;
		}

		private User GetMe()
		{
			var res = RequestManager.SendGETRequest(chatRoot + "/chats/join/favorite");

			if (res == null) { throw new Exception("Could not get user information. Do you have an active internet connection?"); }

			var dom = CQ.Create(RequestManager.GetResponseContent(res));

			var e = dom[".topbar-menu-links a"].First();

			var name = e.Text();
			var id = int.Parse(e.Attr("href").Split('/')[2]);

			return new User(Host, ID, name, id);
		}

		private void SetFkey()
		{
			var res = RequestManager.SendGETRequest(chatRoot + "/rooms/" + ID);

			if (res == null) { throw new Exception("Could not get fkey. Do you have an active internet connection?"); }

			var resContent = RequestManager.GetResponseContent(res);

			var fk = CQ.Create(resContent).GetFkey();

			if (String.IsNullOrEmpty(fk)) { throw new Exception("Could not get fkey. Have Do you have an active internet connection?"); }

			fkey = fk;
		}

		private CookieContainer GetSiteCookies()
		{
			var allCookies = RequestManager.GlobalCookies.GetCookies();
			var siteCookies = new CookieCollection();

			foreach (var cookie in allCookies)
			{
				var cookieDomain = cookie.Domain.StartsWith(".") ? cookie.Domain.Substring(1) : cookie.Domain;

				if ((cookieDomain == Host && cookie.Name.ToLowerInvariant().Contains("usr")) || cookie.Name == "csr")
				{
					siteCookies.Add(cookie);
				}
			}

			var cookies = new CookieContainer();

			cookies.Add(siteCookies);

			return cookies;
		}

		private int GetGlobalEventCount()
		{
			var data = "mode=Events&msgCount=0&fkey=" + fkey;

			RequestManager.CookiesToPass = GetSiteCookies();

			var res = RequestManager.SendPOSTRequest(chatRoot + "/chats/" + ID + "/events", data);

			if (res == null) { throw new Exception("Could not get eventtime for room " + ID + " on " + Host + ". Do you have an active internet conection?"); }

			var resContent = RequestManager.GetResponseContent(res);

			return (int)JObject.Parse(resContent)["time"];
		}

		private string GetSocketURL(int eventTime)
		{
			var data = "roomid=" + ID + "&fkey=" + fkey;

			RequestManager.CookiesToPass = GetSiteCookies();

			var res = RequestManager.SendPOSTRequest(chatRoot + "/ws-auth", data, true, chatRoot + "/rooms/" + ID, chatRoot);

			if (res == null) { throw new Exception("Could not get WebSocket URL. Do you haven an active internet connection?"); }

			var resContent = RequestManager.GetResponseContent(res);

			return (string)JObject.Parse(resContent)["url"] + "?l=" + eventTime;
		}

		private void InitialiseSocket(string socketURL)
		{
			socket = new WebSocket(socketURL, "", null, null, "", chatRoot);

			socket.Error += (o, oo) =>
			{
				var e = (Exception)o;
			};

			socket.DataReceived += (o, oo) =>
			{
				var json = JObject.Parse(Encoding.UTF8.GetString(oo.Data));

				HandleData(json);
			};

			socket.MessageReceived += (o, oo) =>
			{
				var json = JObject.Parse(oo.Message);

				HandleData(json);
			};

			socket.Open();
		}

		# endregion

		# region Incoming message handling methods.

		/// <summary>
		/// WARNING! This method has not yet been fully tested!
		/// </summary>
		private void HandleData(JObject json)
		{
			var data = json["r" + ID];

			if (data == null || data.Type == JTokenType.Null) { return; }

			data = data["e"];

			if (data == null || data.Type == JTokenType.Null) { return; }

			data = data[0];

			if (data == null || data.Type == JTokenType.Null) { return; }

			var eventType = (EventType)(int)(data["event_type"]);

			if ((int)(data["room_id"].Type != JTokenType.Integer ? -1 : data["room_id"]) != ID) { return; }

			switch (eventType)
			{
				case EventType.MessagePosted:
				{
					HandleNewMessage(data);

					return;
				}

				case EventType.MessageReply:
				{
					HandleNewMessage(data);

					return;
				}

				case EventType.UserMentioned:
				{
					HandleNewMessage(data);
					HandleUserMentioned(data);

					return;
				}

				case EventType.MessageEdited:
				{
					HandleEdit(data);

					return;
				}

				case EventType.MessageStarToggled:
				{
					HandleStarToggle(data);

					return;
				}

				case EventType.UserEntered:
				{
					HandleUserJoin(data);

					return;
				}

				case EventType.UserLeft:
				{
					HandleUserLeave(data);

					return;
				}
			}
		}

		private void HandleNewMessage(JToken json)
		{
			var id = (int)json["message_id"];
			var content = Message.GetMessageContent(Host, ID, id);
			var authorName = (string)json["user_name"];
			var authorID = (int)json["user_id"];
			var parentID = (int)(json["parent_id"] ?? -1);

			var message = new Message(Host, content, id, authorName, authorID, parentID);

			AllMessages.Add(message);

			if (NewMessage == null || (authorID == Me.ID && IgnoreOwnEvents)) { return; }

			NewMessage(message);
		}

		/// <summary>
		/// WARNING! This method has noy yet been fully tested!
		/// </summary>
		private void HandleUserMentioned(JToken json)
		{
			var id = (int)json["message_id"];
			var content = Message.GetMessageContent(Host, ID, id);
			var authorName = (string)json["user_name"];
			var authorID = (int)json["user_id"];
			var parentID = (int)(json["parent_id"] ?? -1);

			var message = new Message(Host, content, id, authorName, authorID, parentID);

			AllMessages.Add(message);

			if (NewMessage == null || (authorID == Me.ID && IgnoreOwnEvents)) { return; }

			NewMessage(message);
		}

		/// <summary>
		/// WARNING! This method has noy yet been fully tested!
		/// </summary>
		private void HandleEdit(JToken json)
		{
			var id = (int)json["message_id"];
			var content = Message.GetMessageContent(Host, ID, id);
			var authorName = (string)json["user_name"];
			var authorID = (int)json["user_id"];
			var parentID = (int)(json["parent_id"] ?? -1);

			var currentMessage = new Message(Host, content, id, authorName, authorID, parentID);
			var oldMessage = this[id];

			AllMessages.Remove(oldMessage);
			AllMessages.Add(currentMessage);

			if (MessageEdited == null || (authorID == Me.ID && IgnoreOwnEvents)) { return; }

			MessageEdited(oldMessage, currentMessage);
		}

		private void HandleStarToggle(JToken json)
		{
			var id = (int)json["message_id"];
			var starrerName = (string)json["user_name"];
			var starrerID = (int)json["user_id"];
			var starCount = (int)(json["message_stars"] ?? 0);
			var pinCount = (int)(json["message_owner_stars"] ?? 0);

			var message = this[id];
			var user = new User(Host, ID, starrerName, starrerID);

			if (MessageStarToggled == null || (starrerID == Me.ID && IgnoreOwnEvents)) { return; }

			MessageStarToggled(message, user, starCount, pinCount);
		}

		private void HandleUserJoin(JToken json)
		{
			var username = (string)json["user_name"];
			var userID = (int)json["user_id"];

			var user = new User(Host, ID, username, userID);

			if (UserJoind == null || (userID == Me.ID && IgnoreOwnEvents)) { return; }

			UserJoind(user);
		}

		private void HandleUserLeave(JToken json)
		{
			var username = (string)json["user_name"];
			var userID = (int)json["user_id"];

			var user = new User(Host, ID, username, userID);

			if (UserLeft == null || (userID == Me.ID && IgnoreOwnEvents)) { return; }

			UserLeft(user);
		}

		# endregion
	}
}
