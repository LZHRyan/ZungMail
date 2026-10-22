namespace ZungMail.Core.Domain;

/// <summary>邮箱地址（含显示名）。</summary>
public sealed class EmailAddress
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";

    public EmailAddress() { }

    public EmailAddress(string address, string? name = null)
    {
        Address = address ?? "";
        Name = name ?? "";
    }

    public string Display => string.IsNullOrWhiteSpace(Name) ? Address : $"{Name} <{Address}>";

    public override string ToString() => Display;
}
