using System;
using PortableYouTube;
class UiMessagesTests {
    static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
    static int Main() {
        TestEnvironment.Initialize(true);
        try {
            Assert(UiMessages.Summary(true, 1, 0, 1).StartsWith("已取消"), "Cancellation must not look successful");
            Assert(UiMessages.Summary(false, 1, 1, 1).Contains("失败 1"), "Partial failure must be visible");
            Assert(!UiMessages.Summary(false, 1, 1, 1).StartsWith("全部完成"), "Partial batch shown as complete");
            Assert(UiMessages.Summary(false, 2, 0, 2).StartsWith("全部完成"), "Success summary missing");
            Console.WriteLine("PASS UI completion distinguishes success, partial failure and cancellation");
            return 0;
        } catch (Exception e) { Console.WriteLine("FAIL " + e.Message); return 1; }
    }
}
