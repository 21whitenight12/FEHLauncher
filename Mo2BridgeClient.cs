using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FalloutLauncher
{
    /// <summary>
    /// TCP-клиент для MO2 AI Bridge.
    /// Протокол: JSON с разделителем \n\x00\x00\n, TCP 127.0.0.1:52525.
    /// 
    /// Использование:
    ///   var bridge = new Mo2BridgeClient();
    ///   if (await bridge.ConnectAsync()) {
    ///       var mods = await bridge.CallAsync("modList.allMods");
    ///       await bridge.CallAsync("modList.setActive", "ModName", true);
    ///   }
    ///   bridge.Disconnect();
    /// </summary>
    public class Mo2BridgeClient : IDisposable
    {
        private const int DEFAULT_PORT = 52525;
        private const string DEFAULT_HOST = "127.0.0.1";
        private static readonly byte[] DELIMITER = [0x0A, 0x00, 0x00, 0x0A];
        private const int BUFFER_SIZE = 65536;
        private const int CALL_TIMEOUT_MS = 30_000;
        private const int CONNECT_TIMEOUT_MS = 30_000;

        private TcpClient? _tcpClient;
        private NetworkStream? _stream;
        private volatile bool _connected;
        private volatile bool _running;
        private CancellationTokenSource? _cts;

        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        public string Host { get; }
        public int Port { get; }
        public bool Connected => _connected;

        public Mo2BridgeClient(string host = DEFAULT_HOST, int port = DEFAULT_PORT)
        {
            Host = host;
            Port = port;
        }

        // ======================================================================
        // PUBLIC API
        // ======================================================================

        /// <summary>
        /// Подключиться к bridge и выполнить handshake.
        /// Возвращает true, если успешно.
        /// </summary>
        public async Task<bool> ConnectAsync()
        {
            try
            {
                _tcpClient = new TcpClient();
                using var connectCts = new CancellationTokenSource(CONNECT_TIMEOUT_MS);
                await _tcpClient.ConnectAsync(Host, Port, connectCts.Token);

                if (!_tcpClient.Connected)
                    return false;

                _stream = _tcpClient.GetStream();

                // Server sends handshake first
                var handshake = await ReceiveSingleMessageAsync();
                if (handshake == null)
                    return false;

                if (!handshake.Value.TryGetProperty("type", out var hsType) ||
                    hsType.GetString() != "handshake")
                    return false;

                // Respond to handshake
                var hsResponse = new Dictionary<string, object?>
                {
                    ["type"] = "handshake",
                    ["id"] = Guid.NewGuid().ToString(),
                    ["kwargs"] = new Dictionary<string, object?>
                    {
                        ["name"] = "FEHLauncher",
                        ["subscribe_events"] = new[] { "mod_list_changed", "profile_changed" }
                    }
                };
                await SendRawAsync(hsResponse);

                _connected = true;
                _running = true;
                _cts = new CancellationTokenSource();
                _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));

                return true;
            }
            catch
            {
                Cleanup();
                return false;
            }
        }

        /// <summary>
        /// Вызвать метод API. Блокируется до получения ответа или таймаута.
        /// Пример: CallAsync("modList.allMods") или CallAsync("modList.setActive", "ModName", true)
        /// </summary>
        public async Task<JsonElement?> CallAsync(string method, params object?[]? args)
        {
            if (!_connected)
                throw new InvalidOperationException("Not connected to MO2 Bridge");

            var id = Guid.NewGuid().ToString();
            var tcs = new TaskCompletionSource<JsonElement>();
            _pending[id] = tcs;

            try
            {
                var request = new Dictionary<string, object?>
                {
                    ["type"] = "request",
                    ["id"] = id,
                    ["method"] = method,
                    ["args"] = args ?? Array.Empty<object?>(),
                    ["kwargs"] = new Dictionary<string, object?>()
                };

                await SendRawAsync(request);

                using var timeoutCts = new CancellationTokenSource(CALL_TIMEOUT_MS);
                using var registration = timeoutCts.Token.Register(() => tcs.TrySetCanceled());

                var result = await tcs.Task;
                return result.ValueKind == JsonValueKind.Null || result.ValueKind == JsonValueKind.Undefined
                    ? null
                    : result;
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        /// <summary>Проверить живо ли соединение.</summary>
        public async Task<bool> PingAsync()
        {
            try
            {
                await CallAsync("modList.allMods");
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Отключиться и освободить ресурсы.</summary>
        public void Disconnect()
        {
            _running = false;
            _connected = false;
            _cts?.Cancel();

            foreach (var kvp in _pending)
                kvp.Value.TrySetCanceled();
            _pending.Clear();

            Cleanup();
        }

        public void Dispose() => Disconnect();

        // ======================================================================
        // HELPERS: получение типизированных данных
        // ======================================================================

        /// <summary>Получить список всех установленных модов.</summary>
        public async Task<List<string>> GetAllModsAsync()
        {
            var result = await CallAsync("modList.allMods");
            if (result == null) return new List<string>();

            var list = new List<string>();
            foreach (var item in result.Value.EnumerateArray())
                list.Add(item.GetString() ?? "");
            return list;
        }

        /// <summary>Получить состояние мода: 0=missing, 1=disabled, 2=enabled.</summary>
        public async Task<int> GetModStateAsync(string modName)
        {
            var result = await CallAsync("modList.state", modName);
            return result?.GetInt32() ?? 0;
        }

        /// <summary>Включить или выключить мод через MO2. Бросает исключение при ошибке.</summary>
        public async Task SetModActiveAsync(string modName, bool active)
        {
            // Сначала получаем точное имя мода из MO2 (с учётом регистра)
            string exactName = await ResolveModNameAsync(modName);
            await CallAsync("modList.setActive", exactName, active);
        }

        /// <summary>Найти точное имя мода в MO2 по частичному совпадению (без учёта регистра).</summary>
        public async Task<string> ResolveModNameAsync(string partialName)
        {
            var allMods = await GetAllModsAsync();
            foreach (var mod in allMods)
            {
                if (string.Equals(mod, partialName, StringComparison.OrdinalIgnoreCase))
                    return mod;
            }
            // Если точного совпадения нет — пробуем поиск подстроки
            foreach (var mod in allMods)
            {
                if (mod.Contains(partialName, StringComparison.OrdinalIgnoreCase))
                    return mod;
            }
            // Не нашли — возвращаем как есть, пусть bridge сам разбирается
            return partialName;
        }

        /// <summary>Получить имя текущего профиля.</summary>
        public async Task<string?> GetProfileNameAsync()
        {
            var result = await CallAsync("organizer.profileName");
            return result?.GetString();
        }

        // ======================================================================
        // LOW-LEVEL SEND / RECEIVE
        // ======================================================================

        private async Task SendRawAsync(object message)
        {
            if (_stream == null)
                throw new InvalidOperationException("Not connected");

            var json = JsonSerializer.Serialize(message, JsonOpts);
            var data = Encoding.UTF8.GetBytes(json);

            await _stream.WriteAsync(data.AsMemory(0, data.Length));
            await _stream.WriteAsync(DELIMITER.AsMemory(0, DELIMITER.Length));
            await _stream.FlushAsync();
        }

        /// <summary>Прочитать одно полное сообщение из потока.</summary>
        private async Task<JsonElement?> ReceiveSingleMessageAsync()
        {
            if (_stream == null) return null;

            using var memStream = new MemoryStream();
            var buf = new byte[BUFFER_SIZE];

            try
            {
                while (true)
                {
                    int bytesRead = await _stream.ReadAsync(buf, 0, buf.Length);
                    if (bytesRead == 0) return null;

                    memStream.Write(buf, 0, bytesRead);
                    var totalBytes = memStream.ToArray();

                    int delimIdx = IndexOfDelimiter(totalBytes);
                    if (delimIdx >= 0)
                    {
                        var msgStr = Encoding.UTF8.GetString(totalBytes, 0, delimIdx);
                        return JsonSerializer.Deserialize<JsonElement>(msgStr, JsonOpts);
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Фоновый поток: читает сообщения и диспатчит ответы/события.</summary>
        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            if (_stream == null) return;

            var buffer = new List<byte>();
            var readBuf = new byte[BUFFER_SIZE];

            try
            {
                while (!token.IsCancellationRequested && _running)
                {
                    int bytesRead;
                    try
                    {
                        bytesRead = await _stream.ReadAsync(readBuf, 0, readBuf.Length, token);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (IOException) { break; }
                    catch (ObjectDisposedException) { break; }

                    if (bytesRead == 0) break;

                    buffer.AddRange(readBuf.AsSpan(0, bytesRead).ToArray());

                    // Извлекаем все полные сообщения из буфера
                    while (true)
                    {
                        int delimIdx = IndexOfDelimiter(buffer);
                        if (delimIdx < 0) break;

                        var msgBytes = buffer.Take(delimIdx).ToArray();
                        buffer = buffer.Skip(delimIdx + DELIMITER.Length).ToList();

                        try
                        {
                            var msg = JsonSerializer.Deserialize<JsonElement>(
                                Encoding.UTF8.GetString(msgBytes), JsonOpts);
                            DispatchMessage(msg);
                        }
                        catch { /* skip malformed */ }
                    }

                    // Safety: предотвращаем бесконтрольный рост
                    if (buffer.Count > 1024 * 1024)
                        buffer.Clear();
                }
            }
            catch { }
            finally
            {
                _connected = false;
            }
        }

        private static int IndexOfDelimiter(ReadOnlySpan<byte> data)
        {
            return IndexOfDelimiter(new List<byte>(data.ToArray()));
        }

        private static int IndexOfDelimiter(List<byte> buffer)
        {
            if (buffer.Count < DELIMITER.Length) return -1;
            for (int i = 0; i <= buffer.Count - DELIMITER.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < DELIMITER.Length; j++)
                {
                    if (buffer[i + j] != DELIMITER[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return i;
            }
            return -1;
        }

        // ======================================================================
        // MESSAGE DISPATCH
        // ======================================================================

        private void DispatchMessage(JsonElement msg)
        {
            if (!msg.TryGetProperty("type", out var typeProp)) return;
            var type = typeProp.GetString();

            switch (type)
            {
                case "response":
                    HandleResponse(msg);
                    break;

                case "event":
                    // События пока игнорируем
                    break;

                case "error":
                    if (msg.TryGetProperty("id", out var errId) && errId.ValueKind == JsonValueKind.String)
                    {
                        var id = errId.GetString();
                        if (id != null && _pending.TryGetValue(id, out var tcs))
                            tcs.TrySetException(new Exception(
                                msg.TryGetProperty("message", out var m) ? m.GetString() : "Bridge error"));
                    }
                    break;
            }
        }

        private void HandleResponse(JsonElement msg)
        {
            if (!msg.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.String)
                return;

            var id = idProp.GetString();
            if (id == null || !_pending.TryGetValue(id, out var tcs))
                return;

            if (msg.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            {
                tcs.TrySetException(new Exception(error.GetString() ?? "Unknown bridge error"));
            }
            else if (msg.TryGetProperty("result", out var result))
            {
                tcs.TrySetResult(result);
            }
            else
            {
                // Пустой ответ — считаем null
                tcs.TrySetResult(default);
            }
        }

        // ======================================================================
        // CLEANUP
        // ======================================================================

        private void Cleanup()
        {
            try { _stream?.Close(); } catch { }
            try { _stream?.Dispose(); } catch { }
            try { _tcpClient?.Close(); } catch { }
            try { _tcpClient?.Dispose(); } catch { }
            _stream = null;
            _tcpClient = null;
        }
    }
}
