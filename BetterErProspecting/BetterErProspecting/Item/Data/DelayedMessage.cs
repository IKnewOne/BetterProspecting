using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace BetterErProspecting.Item.Data;

public class DelayedMessage {
	public int chatGroup;
	public string message;
	public EnumChatType ChatType;

	internal DelayedMessage(int chatGroup, string message, EnumChatType chatType) {
		this.chatGroup = chatGroup;
		this.message = message;
		ChatType = chatType;
	}
	internal DelayedMessage(string message) {
		chatGroup = GlobalConstants.InfoLogChatGroup;
		this.message = message;
		ChatType = EnumChatType.Notification;
	}

	public void Send(IServerPlayer sp) {
		sp.SendMessage(chatGroup, message, ChatType);
	}
}

