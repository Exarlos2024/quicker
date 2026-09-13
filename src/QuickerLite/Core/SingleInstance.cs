using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace QuickerLite.Core
{
    /// <summary>
    /// 单实例。
    ///
    /// 坑 8：用户双击两次图标就会开出两个进程 —— 两个托盘图标、
    /// 第二次热键注册必然失败、两份配置互相覆盖。
    ///
    /// 做法：命名 Mutex 做检测；抢到的那份监听命名管道，
    /// 后来的那份发一条消息让它把面板弹出来，然后自己退出。
    /// </summary>
    public sealed class SingleInstance : IDisposable
    {
        private const string MutexName = "QuickerLite.SingleInstance.9F2A";
        private const string PipeName = "QuickerLite.Ipc.9F2A";

        public const string ShowPanelMessage = "SHOW";

        private readonly CancellationTokenSource _cts = new();
        private Mutex? _mutex;

        public bool IsFirstInstance { get; private set; }

        public event Action<string>? MessageReceived;

        public bool TryAcquire()
        {
            try
            {
                _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
                IsFirstInstance = createdNew;
            }
            catch (AbandonedMutexException)
            {
                // 上一个实例被强杀，互斥体被遗弃 —— 我们接管它
                IsFirstInstance = true;
            }

            if (IsFirstInstance) StartServer();
            return IsFirstInstance;
        }

        /// <summary>把消息发给已经在跑的那个实例。</summary>
        public static void NotifyFirstInstance(string message)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(1500);
                using var writer = new StreamWriter(client);
                writer.Write(message);
                writer.Flush();
            }
            catch
            {
                // 对方还没把管道建起来或已经退出，忽略即可
            }
        }

        private void StartServer()
        {
            Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    try
                    {
                        using var server = new NamedPipeServerStream(
                            PipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                        await server.WaitForConnectionAsync(_cts.Token);

                        using var reader = new StreamReader(server);
                        var text = await reader.ReadToEndAsync();

                        if (!string.IsNullOrEmpty(text))
                            MessageReceived?.Invoke(text);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        // 管道被占用等瞬时错误，稍后重试
                        try { await Task.Delay(200, _cts.Token); }
                        catch (OperationCanceledException) { break; }
                    }
                }
            });
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _mutex?.ReleaseMutex(); } catch { }
            try { _mutex?.Dispose(); } catch { }
            try { _cts.Dispose(); } catch { }
        }
    }
}
