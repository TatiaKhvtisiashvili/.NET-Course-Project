// Program.cs
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Course_Project
{
    public class SimpleWebServer
    {
        private const int DefaultPort = 8080;
        private const string WebRootFolderName = "webroot";
        private static readonly string[] AllowedExtensions = { ".html", ".css", ".js", ".ico", ".png", ".jpg", ".gif" };
        private static string _webRootPath = string.Empty; 
        private static string? _logFilePath; 

        public static async Task Main(string[] args)
        {
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
            _webRootPath = Path.GetFullPath(WebRootFolderName);
            _logFilePath = Path.Combine(Directory.GetCurrentDirectory(), "server_requests.log");

            Console.WriteLine($"Working directory: {Directory.GetCurrentDirectory()}");
            Console.WriteLine($"Web root configured to: {_webRootPath}");
            Console.WriteLine($"Request log file: {_logFilePath}");

            Console.WriteLine("=== Course Project: Simple Web Server ===");

            int port = args.Length > 0 && int.TryParse(args[0], out int p) ? p : DefaultPort;
            InitializeWebRoot();

            var server = new TcpListener(IPAddress.Loopback, port);
            server.Start();

            LogMessage($"[STATUS] Server running on http://localhost:{port}");
            LogMessage($"[STATUS] Serving files from: {_webRootPath}");
            if (Directory.Exists(_webRootPath))
            {
                LogMessage($"[STATUS] Webroot contents: {string.Join(", ", Directory.GetFiles(_webRootPath).Select(Path.GetFileName))}");
            }
            else
            {
                LogMessage("[WARNING] Webroot directory does not exist after initialization attempt!");
            }
            LogMessage("[STATUS] Press Ctrl+C to stop...");

            try
            {
                while (true)
                {
                    var client = await server.AcceptTcpClientAsync();
                    _ = HandleClientAsync(client);
                }
            }
            catch (Exception ex)
            {
                LogMessage($"[FATAL] Server crashed: {ex.Message}", isError: true);
            }
            finally
            {
                server.Stop();
                LogMessage("[STATUS] Server stopped.");
            }
        }

        private static void InitializeWebRoot()
        {
            if (!Directory.Exists(_webRootPath))
            {
                Directory.CreateDirectory(_webRootPath);
                LogMessage($"[INIT] Created webroot at: {_webRootPath}");
            }

            string indexPath = Path.Combine(_webRootPath, "index.html");
            if (!File.Exists(indexPath))
            {
                File.WriteAllText(indexPath,
                    @"<!DOCTYPE html><html><head><title>Default Page</title></head><body><h1>Default Page</h1><p>If you see this, webroot/index.html was missing.</p></body></html>");
                LogMessage($"[INIT] Created default index.html in {_webRootPath}");
            }
        }

        private static async Task HandleClientAsync(TcpClient client)
        {
            string clientEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "Unknown Client";
            LogMessage($"[CONNECTION] Client connected: {clientEndPoint}");

            try
            {
                using (client)
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { AutoFlush = false })
                {
                    var requestLine = await reader.ReadLineAsync();
                    if (string.IsNullOrEmpty(requestLine))
                    {
                        LogMessage($"[REQUEST] Empty request line from {clientEndPoint}.", isError: true);
                        return;
                    }

                    LogMessage($"[REQUEST] {clientEndPoint} - {requestLine}");

                    string? headerLine;
                    while (!string.IsNullOrEmpty(headerLine = await reader.ReadLineAsync()))
                    {
                        LogMessage($"[HEADER] {clientEndPoint} - {headerLine}");
                    }

                    var parts = requestLine.Split(' ');
                    if (parts.Length < 3)
                    {
                        await SendErrorResponse(writer, stream, client, 400, "Bad Request", "Malformed request line.");
                        return;
                    }

                    var method = parts[0];
                    var requestedUrlPath = parts[1];

                    if (method != "GET")
                    {
                        await SendErrorResponse(writer, stream, client, 405, "Method Not Allowed", $"Method {method} is not supported. Only GET is allowed.");
                        return;
                    }

                    var resourcePath = requestedUrlPath == "/" ? "index.html" : requestedUrlPath.TrimStart('/');
                    resourcePath = Uri.UnescapeDataString(resourcePath); 

                    resourcePath = resourcePath.Replace("\\", "/");
                    string[] pathSegments = resourcePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

                    if (pathSegments.Contains("..") || pathSegments.Any(s => s.StartsWith("."))) 
                    {
                        LogMessage($"[FORBIDDEN] {clientEndPoint} - Path contains '..' segments: {resourcePath}", isError: true);
                        await SendErrorResponse(writer, stream, client, 403, "Forbidden", "Path contains invalid segments.");
                        return;
                    }

                    string cleanRelativePath = Path.Combine(pathSegments);


                    var fullFilePath = Path.GetFullPath(Path.Combine(_webRootPath, cleanRelativePath));

                    if (!fullFilePath.StartsWith(_webRootPath, StringComparison.OrdinalIgnoreCase))
                    {
                        LogMessage($"[FORBIDDEN] {clientEndPoint} - Directory traversal attempt: {requestedUrlPath} resolved to {fullFilePath}", isError: true);
                        await SendErrorResponse(writer, stream, client, 403, "Forbidden", "Access to the requested path is forbidden (directory traversal).");
                        return;
                    }

                    var extension = Path.GetExtension(fullFilePath).ToLowerInvariant();
                    if (string.IsNullOrEmpty(extension) && File.Exists(fullFilePath + ".html")) 

                    {
                        fullFilePath += ".html";
                        extension = ".html";
                    }
                    else if (Directory.Exists(fullFilePath) && File.Exists(Path.Combine(fullFilePath, "index.html"))) 
                    {
                        fullFilePath = Path.Combine(fullFilePath, "index.html");
                        extension = ".html";
                    }

                    if (!AllowedExtensions.Contains(extension)) 
                    {
                        LogMessage($"[FORBIDDEN] {clientEndPoint} - Unsupported file type: {extension} for {fullFilePath}", isError: true);
                        await SendErrorResponse(writer, stream, client, 403, "Forbidden", $"File type '{extension}' is not supported.");
                        return;
                    }
                 

                    if (!File.Exists(fullFilePath))
                    {
                        LogMessage($"[404] {clientEndPoint} - Not found: {fullFilePath}", isError: true);
                        var dirName = Path.GetDirectoryName(fullFilePath);
                        if (dirName != null && Directory.Exists(dirName))
                        {
                            LogMessage($"[404 DEBUG] {clientEndPoint} - Directory contents of {dirName}: {string.Join(", ", Directory.GetFiles(dirName).Select(Path.GetFileName))}");
                        }
                        await SendErrorResponse(writer, stream, client, 404, "Not Found", $"The resource '{requestedUrlPath}' was not found on this server.");
                        return;
                    }

                    await SendFileResponse(writer, stream, client, fullFilePath, requestedUrlPath);
                    LogMessage($"[200] {clientEndPoint} - Served: {requestedUrlPath} from {fullFilePath}");
                }
            }
            catch (IOException ex) when (ex.InnerException is SocketException se)
            {
                LogMessage($"[NETWORK ERROR] {clientEndPoint} - SocketException: {se.SocketErrorCode} - {se.Message} (Client likely disconnected)", isError: true);
            }
            catch (Exception ex)
            {
                LogMessage($"[ERROR] {clientEndPoint} - Unexpected error handling client: {ex.GetType().Name} - {ex.Message}\n{ex.StackTrace}", isError: true);
                if (client.Connected)
                {
                    try
                    {
                        using var stream = client.GetStream();

                        using var errorWriter = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { AutoFlush = false };
                        await SendErrorResponse(errorWriter, stream, client, 500, "Internal Server Error", "An unexpected error occurred on the server.");
                    }
                    catch (Exception iex)
                    {
                        LogMessage($"[ERROR] {clientEndPoint} - Could not send 500 error response: {iex.Message}", isError: true);
                    }
                }
            }
            finally
            {
                LogMessage($"[CONNECTION] Client disconnected: {clientEndPoint}");
                client.Close(); 
            }
        }

        private static async Task SendFileResponse(StreamWriter writer, NetworkStream stream, TcpClient client, string filePath, string requestedUrl)
        {
            try
            {
                var fileBytes = await File.ReadAllBytesAsync(filePath);
                var mimeType = GetMimeType(Path.GetExtension(filePath));

                await writer.WriteLineAsync($"HTTP/1.1 200 OK");
                await writer.WriteLineAsync($"Content-Type: {mimeType}");
                await writer.WriteLineAsync($"Content-Length: {fileBytes.Length}");
                await writer.WriteLineAsync($"Connection: close");
                await writer.WriteLineAsync(); 
                await writer.FlushAsync(); 

                await stream.WriteAsync(fileBytes, 0, fileBytes.Length);
                await stream.FlushAsync();
            }
            catch (Exception ex)
            {
                LogMessage($"[FILE ERROR] Could not send file {filePath} for {requestedUrl}: {ex.Message}", isError: true);
            }
        }

        private static async Task SendErrorResponse(StreamWriter writer, NetworkStream stream, TcpClient client, int statusCode, string statusMessage, string bodyMessageDetail)
        {
            string htmlBody;
            string errorPagePath = Path.Combine(_webRootPath, "error.html");
            bool useCustomErrorPage = false;

            if (File.Exists(errorPagePath))
            {
                try
                {
                    string customErrorTemplate = await File.ReadAllTextAsync(errorPagePath);
                    htmlBody = customErrorTemplate.Replace("{{ERROR_CODE}}", statusCode.ToString())
                                                  .Replace("{{ERROR_MESSAGE}}", statusMessage)
                                                  .Replace("{{ERROR_DETAIL}}", bodyMessageDetail);
                    useCustomErrorPage = true;
                }
                catch (Exception ex)
                {
                    LogMessage($"[ERROR] Could not load/parse custom error page '{errorPagePath}': {ex.Message}", isError: true);
                    htmlBody = $"<html><head><title>{statusCode} {statusMessage}</title></head><body><h1>{statusCode} {statusMessage}</h1><p>{bodyMessageDetail}</p></body></html>";
                }
            }
            else
            {
                htmlBody = $"<html><head><title>{statusCode} {statusMessage}</title></head><body><h1>{statusCode} {statusMessage}</h1><p>{bodyMessageDetail}</p></body></html>";
            }

            byte[] bodyBytes = Encoding.UTF8.GetBytes(htmlBody);

            try
            {
                if (!client.Connected || !stream.CanWrite)
                {
                    LogMessage($"[ERROR RESPONSE] Client disconnected or stream not writable before sending {statusCode} for '{bodyMessageDetail}'.", isError: true);
                    return;
                }

                await writer.WriteLineAsync($"HTTP/1.1 {statusCode} {statusMessage}");
                await writer.WriteLineAsync($"Content-Type: text/html; charset=utf-8");
                await writer.WriteLineAsync($"Content-Length: {bodyBytes.Length}");
                await writer.WriteLineAsync($"Connection: close");
                await writer.WriteLineAsync(); 
                await writer.FlushAsync(); 

                await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length);
                await stream.FlushAsync();
                LogMessage($"[RESPONSE] Sent {statusCode} {statusMessage}{(useCustomErrorPage ? " (custom page)" : " (inline)")} for: {bodyMessageDetail}");
            }
            catch (IOException ex) when (ex.InnerException is SocketException)
            {
                LogMessage($"[ERROR RESPONSE] Network error sending {statusCode} for '{bodyMessageDetail}' (client likely disconnected): {ex.Message}", isError: true);
            }
            catch (ObjectDisposedException)
            {
                LogMessage($"[ERROR RESPONSE] Stream or client disposed before sending {statusCode} for '{bodyMessageDetail}'.", isError: true);
            }
            catch (Exception ex)
            {
                LogMessage($"[ERROR RESPONSE] Failed to send {statusCode} error for '{bodyMessageDetail}': {ex.GetType().Name} - {ex.Message}", isError: true);
            }
        }

        private static string GetMimeType(string extension)
        {
            return extension.ToLowerInvariant() switch
            {
                ".html" or ".htm" => "text/html; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                ".js" => "application/javascript; charset=utf-8",
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".ico" => "image/x-icon",
                ".gif" => "image/gif", 
                ".txt" => "text/plain; charset=utf-8",
                _ => "application/octet-stream",
            };
        }

        private static readonly object _logLock = new object();
        private static void LogMessage(string message, bool isError = false)
        {
            string logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - {message}";
            Console.WriteLine(logEntry); 

            if (isError)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(logEntry);
                Console.ResetColor();
            }
            else
            {
                Console.WriteLine(logEntry);
            }


            if (!string.IsNullOrEmpty(_logFilePath))
            {
                try
                {
                    lock (_logLock) 
                    {
                        File.AppendAllText(_logFilePath, logEntry + Environment.NewLine);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CRITICAL LOGGING ERROR] Failed to write to log file {_logFilePath}: {ex.Message}");
                }
            }
        }
    }
}