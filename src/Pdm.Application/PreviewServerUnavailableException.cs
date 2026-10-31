namespace Upton.Pdm.Application;

/// <summary>尚未取得可用转图服务器，不计作一次转换失败。</summary>
public sealed class PreviewServerUnavailableException(string message, Exception? innerException = null) : Exception(message, innerException);
