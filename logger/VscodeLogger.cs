using System.Threading;
using System.Threading.Tasks.Dataflow;
using System.Linq;
using System;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
using System.Collections.Generic;

namespace VscodeTestExplorer.DataCollector
{
    [FriendlyName("VsCodeLogger")]
    [ExtensionUri("this://is/a/random/path/that/vstest/apparently/expects/whatever/VscodeLogger")]
    public class TestBla : ITestLoggerWithParameters
    {
        int port;
        public void Initialize(TestLoggerEvents events, string testRunDirectory) { }
        public void Initialize(TestLoggerEvents events, Dictionary<string, string> parameters)
        {
            Console.WriteLine(parameters.Count);
            foreach (var kvp in parameters)
                Console.WriteLine($"{kvp.Key}: {kvp.Value}");

            port = int.Parse(parameters["port"]);
            Console.WriteLine($"Data collector initialized; writing to port {port}.");

            var senderTask = Task.Run(async () => await SendMessages());

            TaskCompletionSource<bool> runEndedTcs = new TaskCompletionSource<bool>();
            runEnded = runEndedTcs.Task;
            void Flush()
            {
                runEndedTcs.SetResult(true);
                senderTask.Wait();
            }

            events.TestRunStart += (sender, e) => StartSendJson(new { type = "testRunStarted" });
            events.TestRunComplete += (sender, e) =>
            {
                StartSendJson(new { type = "testRunComplete" });
                Flush();
            };

            events.DiscoveredTests += (sender, e)
                => StartSendJson(new
                {
                    type = "discovery",
                    discovered = e.DiscoveredTestCases.Select(GetFullName).ToArray()
                });
            events.DiscoveryComplete += (sender, e) => Flush();

            events.TestResult += (sender, e) => StartSendJson(new
            {
                type = "result",
                fullName = GetFullName(e.Result.TestCase),
                outcome = e.Result.Outcome.ToString(),
                message = e.Result.ErrorMessage,
                stackTrace = e.Result.ErrorStackTrace,
            });
        }

        static string GetFullName(TestCase testCase)
            => testCase.GetProperties().Any(kvp => kvp.Key.Id == "XunitTestCase") ?
                testCase.DisplayName : testCase.FullyQualifiedName;

        async Task SendString(string str)
        {
            // Console.WriteLine("Sending: " + str);

            using TcpClient client = new TcpClient();
            await client.ConnectAsync("localhost", port);
            await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(str));
        }

        BufferBlock<object> toBeSent = new BufferBlock<object>();
        void StartSendJson<T>(T obj)
        {
            toBeSent.Post(obj);
        }

        Task runEnded;
        async Task SendAggregatedMessageAsync()
        {
            var message = new List<object>();
            CancellationTokenSource cts = new CancellationTokenSource();
            var receiveTask = toBeSent.ReceiveAsync(cts.Token);
            await Task.WhenAny(new[] { receiveTask, runEnded });
            if (runEnded.IsCompleted)
            {
                Console.WriteLine($"Run ended.");
                cts.Cancel();
                if (receiveTask.IsCompletedSuccessfully)
                    message.Add(receiveTask.Result);
            }
            else
            {
                message.Add(await receiveTask);
                Console.WriteLine($"Received a message - waiting for further messages...");
                await Task.Delay(100);
            }
            if (toBeSent.TryReceiveAll(out var items))
                message.AddRange(items);
            if (message.Count > 0)
            {
                Console.WriteLine($"Sending {message.Count} messages...");
                using TcpClient client = new TcpClient();
                await client.ConnectAsync("localhost", port);
                await JsonSerializer.SerializeAsync(client.GetStream(), message.ToArray(), typeof(object[]));
                client.Close();
                Console.WriteLine($"Done.");
            }
        }

        async Task SendMessages()
        {
            Console.WriteLine("Sender task started.");
            while (!runEnded.IsCompleted)
            {
                try
                {
                    await SendAggregatedMessageAsync();
                }
                catch (Exception e)
                {
                    Console.WriteLine("Sending messages failed.");
                    Console.WriteLine(e.ToString());
                    Console.WriteLine(e.StackTrace);
                }
            }
            Console.WriteLine("Sender task finished.");
        }
    }
}
