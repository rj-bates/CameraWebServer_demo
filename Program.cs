using CameraFlashWebAppServer;
using System.Net.WebSockets;
using System.Text;
using System.Collections.Concurrent;
using System.Text.Json;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);
// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("./logs/webserver.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Host.UseSerilog();

var app = builder.Build();

// Enable WebSocket support
var webSocketOptions = new WebSocketOptions()
{
    KeepAliveInterval = TimeSpan.FromMinutes(2),
};
var connectedClients = new ConcurrentDictionary<string, WebSocket>();
app.UseWebSockets(webSocketOptions);

// Handle WebSocket connections
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/ws")
    {
        if (context.WebSockets.IsWebSocketRequest)
        {
            var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            var clientId = Guid.NewGuid().ToString();
            connectedClients.TryAdd(clientId, webSocket);
            Log.Information($"WebSocket connection established for client: {clientId}");

            try
            {
                await HandleWebSocketCommunication(context, webSocket, clientId);
            }
            catch (Exception ex)
            {
                Log.Error($"Error in WebSocket communication: {ex.Message}");
            }
            finally
            {
                connectedClients.TryRemove(clientId, out _);
                Log.Information($"WebSocket connection closed for client: {clientId}");
            }
        }
        else
        {
            context.Response.StatusCode = 400;
        }
    }
    else
    {
        await next();
    }
});

app.Run();
// Add this after app.Run();
Log.Information("WebSocket server is running. Press any key to exit.");
Console.ReadKey();
Log.CloseAndFlush();

// Method to handle WebSocket communication
async Task HandleWebSocketCommunication(HttpContext context, WebSocket webSocket, string clientId)
{
    var buffer = new byte[1024 * 4];
    WebSocketReceiveResult result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

    var flashController = new FlashControllerApi(Log.Logger);
    
    while (!result.CloseStatus.HasValue)
    {
        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
        Log.Information($"Received raw message from client {clientId}: {message}");


        try
        {
            var jsonMessage = JsonSerializer.Deserialize<JsonMessage>(message, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            Log.Information($"Deserialized message - Type: {jsonMessage?.Type}, Command: {jsonMessage?.Command}");

            if (jsonMessage?.Type == "command")
            {
                switch (jsonMessage.Command)
                {
                    case "TakePhoto":
                        var (filePath, imageData, errorMsg1) = await flashController.TakePhotoAsync(FlashMode.ON, CameraType.BACK, 1920, 1080);
                        if (string.IsNullOrEmpty(errorMsg1))
                        {
                            await SendJsonResponse(webSocket, new
                            {
                                type = "photo",
                                filePath = filePath,
                                imageData = Convert.ToBase64String(imageData)
                            });
                        }
                        else
                        {
                            await SendJsonResponse(webSocket, new { type = "error", message = errorMsg1 });
                        }
                        break;
                    case "TakePhotoNative":
                        var (nativeFilePath, nativeImageData, errorMsg) = await flashController.CapturePhotoUsingNativeCameraAsync();
                        if (string.IsNullOrEmpty(errorMsg))
                        {
                            await SendJsonResponse(webSocket, new { type = "photo", filePath = nativeFilePath, imageData = Convert.ToBase64String(nativeImageData) });
                        }
                        else
                        {
                            await SendJsonResponse(webSocket, new { type = "error", message = errorMsg });
                        }
                        break;
                    case "FlashOn":
                        await SetFlashMode(flashController, FlashMode.ON);
                        await SendJsonResponse(webSocket, new { type = "flash", status = "on" });
                        break;
                    case "FlashOff":
                        await SetFlashMode(flashController, FlashMode.OFF);
                        await SendJsonResponse(webSocket, new { type = "flash", status = "off" });
                        break;
                    default:
                        Log.Warning($"Unknown command: {jsonMessage.Command}");
                        await SendJsonResponse(webSocket, new { type = "error", message = "Unknown command" });
                        break;
                }
            }
            else
            {
                Log.Warning($"Unknown command: {jsonMessage.Command}");
                await SendJsonResponse(webSocket, new { type = "error", message = "Unknown command" });
            }
        }
        catch (System.Text.Json.JsonException jsonEx)
        {
            Log.Error($"Error parsing JSON: {jsonEx.Message}");
            Log.Error($"JSON Error details: {jsonEx}");
            await SendJsonResponse(webSocket, new { type = "error", message = "Invalid JSON format" });
        }
        catch (Exception ex)
        {
            Log.Error($"Error processing message: {ex.Message}");
            await SendJsonResponse(webSocket, new { type = "error", message = "Error processing command" });
        }

        result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
    }

    await webSocket.CloseAsync(result.CloseStatus.Value, result.CloseStatusDescription, CancellationToken.None);
}

// Method to take a photo using the FlashControllerApi
async Task<(string filePath, byte[] imageData, string errorMsg)> TakePhotoAsync(FlashControllerApi flashController)
{
    var (filePath, imageData, errorMsg) = await flashController.TakePhotoAsync(FlashMode.ON, CameraType.BACK, 1920, 1080);
    return (filePath, imageData, errorMsg);
}

// Method to set flash mode
async Task SetFlashMode(FlashControllerApi flashController, FlashMode flashMode)
{
    await flashController.TakePhotoAsync(flashMode, CameraType.BACK, 1920, 1080); // Use the same API to set the flash mode
}

async Task SendJsonResponse(WebSocket webSocket, object data)
{
    var json = System.Text.Json.JsonSerializer.Serialize(data);
    var bytes = Encoding.UTF8.GetBytes(json);
    await webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
}

class JsonMessage
{
    public string Type { get; set; }
    public string Command { get; set; }
}