using ZungMail.Core.Domain;
using ZungMail.Infrastructure.Security;
using ZungMail.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ZungMail.Tests;

/// <summary>账户凭据迁移：旧 AES 密文保持可读，并在下一次保存时自动改写为 DPAPI 格式。</summary>
[Trait("Category", "Integration")]
public sealed class AccountCredentialMigrationTests : IDisposable
{
    private readonly string _root;
    private readonly SqliteConnectionFactory _factory;

    public AccountCredentialMigrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ZungMailTests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        _factory = new SqliteConnectionFactory(paths);
        new DatabaseInitializer(_factory).Initialize();
    }

    [Fact]
    public async Task Update_旧AES密文读取后下一次保存切换为DPAPI()
    {
        var legacy = new AesCredentialProtector(new AppPaths(_root));
        var legacyRepository = new SqliteAccountRepository(_factory, legacy);
        var id = await legacyRepository.AddAsync(NewAccount(), CancellationToken.None);

        var legacyStored = await ReadSecretEncAsync(id);
        Assert.False(legacyStored!.StartsWith(DpapiCredentialProtector.Prefix, StringComparison.Ordinal));

        var repository = new SqliteAccountRepository(_factory, new DpapiCredentialProtector(new AppPaths(_root)));
        var account = await repository.GetAsync(id, CancellationToken.None);
        Assert.NotNull(account);
        Assert.Equal("auth-code", account!.Secret);

        await repository.UpdateAsync(account, CancellationToken.None);

        Assert.StartsWith(DpapiCredentialProtector.Prefix, await ReadSecretEncAsync(id));
    }

    [Fact]
    public async Task 主账户_第一个账户自动成为主账户且可切换()
    {
        var repository = new SqliteAccountRepository(_factory, new AesCredentialProtector(new AppPaths(_root)));
        var first = await repository.AddAsync(NewAccount(), CancellationToken.None);
        var second = await repository.AddAsync(NewAccount(), CancellationToken.None);

        var initial = await repository.GetAllAsync(CancellationToken.None);
        Assert.Equal(2, initial.Count);
        Assert.Equal(first, initial[0].Id);
        Assert.True(initial[0].IsPrimary);
        Assert.False(initial[1].IsPrimary);

        // 切换主账户：新的排最前，旧的降为副账户，全局只有一个主账户
        await repository.SetPrimaryAsync(second, CancellationToken.None);

        var reloaded = await repository.GetAllAsync(CancellationToken.None);
        Assert.Equal(second, reloaded[0].Id);
        Assert.True(reloaded[0].IsPrimary);
        Assert.Single(reloaded, account => account.IsPrimary);
        Assert.False(Assert.Single(reloaded, account => account.Id == first).IsPrimary);
    }

    private async Task<string?> ReadSecretEncAsync(long id)
    {
        await using var connection = await _factory.CreateAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT secret_enc FROM accounts WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        return (string?)await command.ExecuteScalarAsync();
    }

    private static Account NewAccount() => new()
    {
        Name = "测试账户",
        Email = "user@example.com",
        DisplayName = "User",
        Username = "user@example.com",
        Secret = "auth-code",
        AuthKind = AuthKind.AuthCode,
        ImapHost = "imap.example.com",
        ImapPort = 993,
        ImapSecure = SecureMode.Ssl,
        SmtpHost = "smtp.example.com",
        SmtpPort = 465,
        SmtpSecure = SecureMode.Ssl,
        IsEnabled = true
    };

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
