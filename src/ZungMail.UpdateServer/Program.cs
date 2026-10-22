using ZungMail.UpdateServer;

var app = ServerBootstrap.Build(args);
app.Run();

/// <summary>供集成测试定位程序集（本文件不参与客户端构建）。</summary>
public partial class Program;
