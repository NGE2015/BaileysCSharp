using Org.BouncyCastle.Cms;
using Proto;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BaileysCSharp.Core.Models;
using BaileysCSharp.Core.Models.Sessions;
using BaileysCSharp.Core.Signal;
using BaileysCSharp.Exceptions;
using static BaileysCSharp.Core.Utils.JidUtils;
using BaileysCSharp.Core.WABinary;
using BaileysCSharp.Core.Logging;

namespace BaileysCSharp.Core
{

    public class MessageDecoder
    {
        /// <summary>
        /// Callback action to notify when caller phone number is extracted from a message
        /// Allows the caller to cache the phone number for later use
        /// </summary>
        public static Action<string, string> OnCallerPhoneNumberExtracted { get; set; }

        public static MessageDecryptor DecryptMessageNode(BinaryNode stanza, string meId, string meLid, SignalRepository repository, DefaultLogger logger)
        {

            string chatId = "";
            string msgType = "";
            string author = "";

            var msgId = stanza.attrs["id"];
            var from = stanza.attrs["from"];
            var participant = stanza.getattr("participant");
            var recipient = stanza.getattr("recipient");

            // Extract phone numbers from message attributes (Bailey's v7 feature)
            // These contain the actual phone number when messaging unknown contacts (@lid format)
            var callerPn = stanza.getattr("caller_pn");      // Phone number for incoming calls/messages from unknown
            var senderPn = stanza.getattr("sender_pn");      // Phone number explicitly shared by sender

            if (IsJidUser(from))
            {
                if (!string.IsNullOrWhiteSpace(recipient))
                {
                    if (!AreJidsSameUser(from, meId))
                    {
                        throw new Boom("receipient present, but msg not from me", Events.DisconnectReason.MissMatch);
                    }
                    chatId = recipient;
                }
                else
                {
                    chatId = from;
                }
                msgType = "chat";
                author = from;
            }
            else if (IsLidUser(from))
            {
                if (!string.IsNullOrWhiteSpace(recipient))
                {
                    if (!AreJidsSameUser(from, meLid))
                    {
                        throw new Boom("receipient present, but msg not from me", Events.DisconnectReason.MissMatch);
                    }
                    chatId = recipient;
                }
                else
                {
                    chatId = from;
                }
                msgType = "chat";
                author = from;
            }
            else if (IsJidGroup(from))
            {
                if (participant == null)
                {
                    throw new Boom("No participant in group message", Events.DisconnectReason.MissMatch);
                }
                else
                {
                    msgType = "group";
                    author = participant;
                    chatId = from;
                }
            }
            else if (IsBroadcast(from))
            {
                if (participant == null)
                {
                    throw new Boom("No participant in group message", Events.DisconnectReason.MissMatch);
                }
                else
                {
                    var isParticipantMe = AreJidsSameUser(meId, participant);

                    if (IsJidStatusBroadcast(from))
                    {
                        msgType = isParticipantMe ? "direct_peer_status" : "other_status";
                    }
                    else
                    {
                        msgType = isParticipantMe ? "peer_broadcast" : "other_broadcast";
                    }

                    chatId = from;
                    author = participant;
                }
            }
            else if (IsJidNewsletter(from))
            {
                chatId = from;
            }

            // ===== NEW: NORMALIZE @lid FORMAT TO REAL PN =====
            // If message came from @lid and we have caller_pn, use PN instead
            // This ensures entire system works with PN format, not @lid
            bool wasLidNormalized = false;
            if (IsLidUser(chatId) && !string.IsNullOrEmpty(callerPn))
            {
                logger.Error($"[LID_NORMALIZATION] Normalizing @lid to PN - Before: chatId={chatId}, author={author}");

                chatId = callerPn;      // Replace @lid with actual phone number
                author = callerPn;      // Also update author to match

                wasLidNormalized = true;
                logger.Error($"[LID_NORMALIZATION] Normalization complete - After: chatId={chatId}, author={author}");
            }
            // ===== END: LID NORMALIZATION =====

            var notify = stanza.getattr("notify");
            bool fromMe;
            if (IsLidUser(from))
            {
                fromMe = AreJidsSameUser(meLid, !string.IsNullOrWhiteSpace(participant) ? participant : from);
            }
            else
            {
                fromMe = AreJidsSameUser(meId, !string.IsNullOrWhiteSpace(participant) ? participant : from);
            }

            var fullMessage = new WebMessageInfo()
            {
                Key = new MessageKey()
                {
                    RemoteJid = chatId,
                    Id = msgId,
                    FromMe = fromMe,
                    Participant = participant ?? "",
                },
                PushName = notify ?? "",
                Broadcast = IsBroadcast(from)

            };

            if (fromMe)
            {
                fullMessage.Status = WebMessageInfo.Types.Status.ServerAck;
            }


            var msgDecryptor = new MessageDecryptor(repository)
            {
                Stanza = stanza,
                Msg = fullMessage,
                Author = author,
                Category = stanza.getattr("category") ?? "",
                Sender = msgType == "chat" ? author : chatId,
                CallerPhoneNumber = callerPn,           // Store extracted phone number for @lid messages
                SenderPhoneNumber = senderPn            // Store phone number from SharePhoneNumber protocol
            };

            // Log the final message state after normalization
            if (wasLidNormalized)
            {
                logger.Error($"[LID_NORMALIZATION_SUMMARY] Message normalized successfully - MsgId={msgId}, NormalizedRemoteJid={fullMessage.Key.RemoteJid}, PushName={notify}, CallerPN={callerPn}");
            }

            // Notify subscribers about extracted phone numbers for caching
            if (!string.IsNullOrEmpty(callerPn) && !string.IsNullOrEmpty(msgId))
            {
                try
                {
                    OnCallerPhoneNumberExtracted?.Invoke(msgId, callerPn);
                }
                catch (Exception ex)
                {
                    logger.Error($"Error notifying caller phone number extraction: {ex.Message}");
                }
            }

            if (!string.IsNullOrEmpty(senderPn) && !string.IsNullOrEmpty(msgId))
            {
                try
                {
                    OnCallerPhoneNumberExtracted?.Invoke($"{msgId}:sender", senderPn);
                }
                catch (Exception ex)
                {
                    logger.Error($"Error notifying sender phone number extraction: {ex.Message}");
                }
            }

            return msgDecryptor;
        }

    }
}
