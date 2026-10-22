using System.Reflection;
using ZungMail.Application.Services;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ZungMail.Tests;

/// <summary>文件夹管理服务的名称校验与系统文件夹保护；远端端口用替身记录是否被调用。</summary>
[Trait("Category", "Unit")]
public sealed class MailFolderServiceTests
{
    [Theory]
    [InlineData("", FolderOperationStatus.InvalidName)]
    [InlineData("   ", FolderOperationStatus.InvalidName)]
    [InlineData("a/b", FolderOperationStatus.InvalidName)]
    [InlineData("con:", FolderOperationStatus.InvalidName)]
    public async Task CreateAsync_非法名称_返回校验错误且不调用远端(
        string name, FolderOperationStatus expected)
    {
        var admin = new RecordingAdmin();
        var service = new MailFolderService(
            DispatchProxy.Create<IFolderRepository, NoopProxy>(), DispatchProxy.Create<IMessageRepository, EmptyMessageProxy>(), admin, NullLogger<MailFolderService>.Instance);

        var result = await service.CreateAsync(1, null, name);

        Assert.Equal(expected, result.Status);
        Assert.Equal(0, admin.CreateCalls);
    }

    [Fact]
    public async Task RenameAsync_系统文件夹_拒绝操作()
    {
        var admin = new RecordingAdmin();
        var service = new MailFolderService(
            DispatchProxy.Create<IFolderRepository, NoopProxy>(), DispatchProxy.Create<IMessageRepository, EmptyMessageProxy>(), admin, NullLogger<MailFolderService>.Instance);

        var result = await service.RenameAsync(1, new MailFolder
        {
            Id = 3,
            AccountId = 1,
            FullName = "INBOX",
            Kind = FolderKind.Inbox
        }, "新名称");

        Assert.Equal(FolderOperationStatus.SystemFolderProtected, result.Status);
        Assert.Equal(0, admin.RenameCalls);
    }

    [Fact]
    public async Task DeleteAsync_系统文件夹_拒绝操作()
    {
        var admin = new RecordingAdmin();
        var service = new MailFolderService(
            DispatchProxy.Create<IFolderRepository, NoopProxy>(), DispatchProxy.Create<IMessageRepository, EmptyMessageProxy>(), admin, NullLogger<MailFolderService>.Instance);

        var result = await service.DeleteAsync(new MailFolder
        {
            Id = 4,
            AccountId = 1,
            FullName = "已发送",
            Kind = FolderKind.Sent
        });

        Assert.Equal(FolderOperationStatus.SystemFolderProtected, result.Status);
        Assert.Equal(0, admin.DeleteCalls);
    }

    private sealed class RecordingAdmin : IMailFolderAdminService
    {
        public int CreateCalls { get; private set; }
        public int RenameCalls { get; private set; }
        public int DeleteCalls { get; private set; }

        public Task<MailFolder> CreateFolderAsync(long accountId, string? parentFullName, string displayName, CancellationToken ct = default)
        {
            CreateCalls++;
            return Task.FromResult(new MailFolder { AccountId = accountId, FullName = displayName, DisplayName = displayName });
        }

        public Task<string> RenameFolderAsync(long accountId, string fullName, string newDisplayName, CancellationToken ct = default)
        {
            RenameCalls++;
            return Task.FromResult(newDisplayName);
        }

        public Task DeleteFolderAsync(long accountId, string fullName, CancellationToken ct = default)
        {
            DeleteCalls++;
            return Task.CompletedTask;
        }
    }

    /// <summary>校验失败路径不会访问仓储，任何调用都返回默认值即可。</summary>
    private class NoopProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }

    /// <summary>删除前的空文件夹检查：返回 0 封邮件。</summary>
    private class EmptyMessageProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => typeof(Task<int>).IsAssignableFrom(targetMethod?.ReturnType) ? Task.FromResult(0) : null;
    }
}
