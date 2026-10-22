namespace ZungMail.Application.Modularity;

/// <summary>为宿主导航区域贡献业务入口的契约；具体 WPF 控件由表示层根据定义呈现。</summary>
public interface INavigationContributor
{
    IEnumerable<NavigationItemDefinition> GetNavigationItems();
}

/// <summary>与 UI 框架无关的导航项定义。</summary>
public sealed record NavigationItemDefinition(
    string Id,
    string Title,
    string IconKey,
    int Order = 0,
    string? Group = null);
