using System.Net.WebSockets;
using System.Text;
using Azure.Communication.CallAutomation;
using Microsoft.Extensions.Logging;

namespace CallAutomation.AzureAI.VoiceLive
{
    public class AcsMediaStreamingHandler
    {
        private readonly WebSocket m_webSocket;
        private readonly IConfiguration m_configuration;
        private readonly ILogger<AcsMediaStreamingHandler> m_logger;
        private readonly ILoggerFactory m_loggerFactory;
        private AzureVoiceLiveService m_aiServiceHandler = null!;
        private CancellationTokenSource m_cts = new();

        public AcsMediaStreamingHandler(
            WebSocket webSocket,
            IConfiguration configuration,
            ILogger<AcsMediaStreamingHandler> logger,
            ILoggerFactory loggerFactory)
        {
            m_webSocket = webSocket;
            m_configuration = configuration;
            m_logger = logger;
            m_loggerFactory = loggerFactory;
        }

        public async Task ProcessWebSocketAsync()
        {
            if (m_webSocket == null) return;

            try
            {
                m_aiServiceHandler = new AzureVoiceLiveService(
                    this,
                    m_configuration,
                    m_loggerFactory.CreateLogger<AzureVoiceLiveService>());

                // hang_up tool callback — terminate the bridge so ACS tears down the call.
                m_aiServiceHandler.OnHangUp(async reason =>
                {
                    m_logger.LogInformation("AI requested hang up: {Reason}", reason);
                    m_cts.Cancel();
                    if (m_webSocket.State == WebSocketState.Open)
                    {
                        await m_webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "AI hangup", CancellationToken.None);
                    }
                });

                await m_aiServiceHandler.InitializeAsync();
                await StartReceivingFromAcsMediaWebSocket();
            }
            catch (Exception ex)
            {
                m_logger.LogError(ex, "Error in ACS media streaming handler");
            }
            finally
            {
                if (m_aiServiceHandler != null)
                {
                    await m_aiServiceHandler.Close();
                }
            }
        }

        public async Task SendMessageAsync(string message)
        {
            if (m_webSocket?.State != WebSocketState.Open) return;
            var bytes = Encoding.UTF8.GetBytes(message);
            await m_webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        }

        private async Task WriteToAzureFoundryAIServiceInputStream(string data)
        {
            var input = StreamingData.Parse(data);
            if (input is AudioData audioData && !audioData.IsSilent)
            {
                await m_aiServiceHandler.SendAudioToExternalAI(audioData.Data.ToArray());
            }
        }

        private async Task StartReceivingFromAcsMediaWebSocket()
        {
            if (m_webSocket == null) return;
            try
            {
                while (m_webSocket.State == WebSocketState.Open && !m_cts.IsCancellationRequested)
                {
                    var buffer = new byte[2048];
                    var receiveResult = await m_webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), m_cts.Token);
                    if (receiveResult.MessageType == WebSocketMessageType.Close)
                    {
                        m_logger.LogInformation("ACS WebSocket closed by remote");
                        break;
                    }
                    var data = Encoding.UTF8.GetString(buffer, 0, receiveResult.Count);
                    await WriteToAzureFoundryAIServiceInputStream(data);
                }
            }
            catch (OperationCanceledException)
            {
                m_logger.LogInformation("ACS WebSocket receive cancelled");
            }
            catch (Exception ex)
            {
                m_logger.LogError(ex, "Exception while receiving from ACS WebSocket");
            }
        }
    }
}
