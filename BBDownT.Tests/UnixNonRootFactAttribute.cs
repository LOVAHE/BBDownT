namespace BBDownT.Tests;

public sealed class UnixNonRootFactAttribute : FactAttribute
{
    public UnixNonRootFactAttribute()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
            Skip = "文件权限测试需要在 Unix 上以非 root 用户运行";
    }
}
