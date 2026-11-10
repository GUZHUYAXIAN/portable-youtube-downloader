using System;
using System.Text;
using System.Web.Script.Serialization;

namespace PortableYouTube.StageBridge
{
    public static class StageBridgeProgram
    {
        public static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            var json = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024, RecursionLimit = 64 };
            string action = args.Length >= 3 ? args[2] : null;
            StageBridgeService service = null;
            ParentProcessGuard parentGuard = null;
            ConsoleCancelEventHandler cancel = delegate(object sender, ConsoleCancelEventArgs e) {
                e.Cancel = true;
                if (service != null) service.Cancel();
            };
            Console.CancelKeyPress += cancel;
            try {
                if (args.Length < 3 || args[0] != "--workspace") throw new ArgumentException("用法：StageBridge.exe --workspace <项目目录> submit <manifest.json> | run <batchId> [--retry-failed] | status [batchId]");
                if ((action == "submit" && args.Length != 4) || (action == "run" && (args.Length < 4 || args.Length > 5 || (args.Length == 5 && args[4] != "--retry-failed"))) ||
                    (action == "status" && args.Length != 3 && args.Length != 4) || (action != "submit" && action != "run" && action != "status"))
                    throw new ArgumentException("不支持的命令或参数数量；请使用 submit、run 或 status。");
                parentGuard = ParentProcessGuard.FromEnvironment();
                service = new StageBridgeService(args[1]);
                service.Log = message => Console.Error.WriteLine(message);
                BridgeReceipt receipt;
                if (action == "submit") receipt = service.SubmitManifest(args[3]);
                else if (action == "run") receipt = service.Run(args[3], args.Length == 5);
                else receipt = service.GetStatus(args.Length == 4 ? args[3] : null);
                Console.WriteLine(json.Serialize(receipt));
                return receipt.cancelled ? 130 : receipt.ok ? 0 : 1;
            } catch (Exception e) {
                Console.WriteLine(json.Serialize(new BridgeReceipt { action = action, ok = false, error = e.Message, generatedUtc = DateTime.UtcNow.ToString("o") }));
                return 2;
            } finally {
                Console.CancelKeyPress -= cancel;
                if (parentGuard != null) parentGuard.Dispose();
            }
        }
    }
}
