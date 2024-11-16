using System.Diagnostics.CodeAnalysis;
using BaileysCSharp.Core.Models;

namespace BaileysCSharp.Core.Sockets
{

    public class WASocket : NewsletterSocket
    {
        public WASocket([NotNull] SocketConfig config) : base(config)
        {
        }
        public void DisconnectSession()
        {
            try
            {
                // Call the existing End method indirectly
                WS.Disconnect();
                
                Logger.Info("Session disconnected successfully.");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error while disconnecting the session.");
            }
        }
      

    }
}
