using System;
using System.IO;
using static BBDownT.Core.Logger;

namespace BBDownT;

internal static class LegacyLocalFileMigration
{
    private static readonly (string OldName, string NewName)[] Files =
    [
        ("BBDown.config", "BBDownT.config"),
        ("BBDown.data", "BBDownT.data"),
        ("BBDownTV.data", "BBDownTTV.data"),
        ("BBDownApp.data", "BBDownTApp.data"),
        ("BBDown.archives", "BBDownT.archives")
    ];

    internal interface IFileOperations
    {
        bool FileExists(string path);
        bool DirectoryExists(string path);
        void Copy(string sourceFileName, string destFileName);
        void Move(string sourceFileName, string destFileName);
        void Delete(string path);
    }

    private sealed class RealFileOperations : IFileOperations
    {
        public bool FileExists(string path) => File.Exists(path);
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public void Copy(string sourceFileName, string destFileName) => File.Copy(sourceFileName, destFileName);
        public void Move(string sourceFileName, string destFileName) => File.Move(sourceFileName, destFileName);
        public void Delete(string path) => File.Delete(path);
    }

    // Called only by the explicit migrate command, never during normal startup.
    internal static int Run(string directory, string? timestamp = null, IFileOperations? operations = null)
    {
        operations ??= new RealFileOperations();
        var migrated = 0;
        var failed = 0;
        foreach (var (oldName, newName) in Files)
        {
            try
            {
                var oldPath = Path.Combine(directory, oldName);
                if (!operations.FileExists(oldPath)) continue;
                var newPath = Path.Combine(directory, newName);
                if (operations.FileExists(newPath))
                {
                    Log($"{newName}已存在，跳过{oldName}");
                    continue;
                }

                var tempPath = $"{newPath}.migrating-{Guid.NewGuid():N}";
                var stamp = timestamp ?? DateTime.Now.ToString("yyyyMMddHHmmss");
                var backupPath = $"{oldPath}.migrated-{stamp}";
                var index = 1;
                while (operations.FileExists(backupPath) || operations.DirectoryExists(backupPath))
                    backupPath = $"{oldPath}.migrated-{stamp}-{index++}";

                var backupCreated = false;
                try
                {
                    operations.Copy(oldPath, tempPath);
                    operations.Move(oldPath, backupPath);
                    backupCreated = true;
                    operations.Move(tempPath, newPath);
                }
                catch
                {
                    TryDelete(operations, tempPath);
                    if (backupCreated)
                    {
                        if (!operations.FileExists(oldPath) && operations.FileExists(backupPath))
                            TryMove(operations, backupPath, oldPath);
                    }
                    throw;
                }

                migrated++;
                Log($"已迁移{oldName} -> {newName}，旧文件备份为{Path.GetFileName(backupPath)}");
            }
            catch (Exception error)
            {
                failed++;
                LogWarn($"迁移{oldName}失败: {error.Message}");
            }
        }
        Log($"迁移完成：成功{migrated}个，失败{failed}个");
        return failed == 0 ? 0 : 1;
    }

    private static void TryDelete(IFileOperations operations, string path)
    {
        try
        {
            if (!operations.FileExists(path)) return;
            operations.Delete(path);
            if (operations.FileExists(path))
                LogWarn($"迁移临时文件仍然存在 {path}，请手动检查并删除");
        }
        catch (Exception error)
        {
            LogWarn($"无法清理迁移临时文件 {path}: {error.Message}，请手动检查并删除");
        }
    }

    private static void TryMove(IFileOperations operations, string sourcePath, string destinationPath)
    {
        try
        {
            operations.Move(sourcePath, destinationPath);
            if (operations.FileExists(sourcePath))
                LogWarn($"迁移备份仍未恢复 {sourcePath} -> {destinationPath}，请手动检查备份文件");
        }
        catch (Exception error)
        {
            LogWarn($"无法恢复迁移备份 {sourcePath} -> {destinationPath}: {error.Message}，请手动检查备份文件");
        }
    }
}
