namespace Upton.Pdm.Domain;

/// <summary>硬件确认时固定的铭牌生产内容；设备尺寸不是铭牌制作尺寸。</summary>
public sealed record NameplateContent(string Name, string Model, string SerialNumber,
    string PowerSupply, decimal RatedPower, string Dimensions, string AirPressure,
    string Weight, DateOnly FactoryDate, string ConfirmedBy, DateTimeOffset ConfirmedAt);
