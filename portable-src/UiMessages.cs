using System;
namespace PortableYouTube {
    public static class UiMessages {
        public static string Summary(bool cancelled, int succeeded, int failed, int files) {
            if (cancelled) return "已取消 · 已验证 " + files + " 个 MP4；未完成文件可在下次继续下载";
            if (failed > 0) return "下载结束 · 成功 " + succeeded + " 条，失败 " + failed + " 条 · 已验证 " + files + " 个 MP4";
            if (succeeded == 0 || files == 0) return "未完成 · 没有得到通过音视频检查的 MP4";
            return "全部完成 · 成功 " + succeeded + " 条链接 · 已验证 " + files + " 个有声 MP4";
        }
    }
}
