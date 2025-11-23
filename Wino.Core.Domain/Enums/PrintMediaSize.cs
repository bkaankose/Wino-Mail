namespace Wino.Core.Domain.Enums;

/// <summary>
/// Print media size options.
/// </summary>
public enum PrintMediaSize
{
    /// <summary>
    /// Default media size.
    /// </summary>
    Default = 0,
    
    /// <summary>
    /// Letter size (8.5 x 11 inches).
    /// </summary>
    NorthAmericaLetter = 1,
    
    /// <summary>
    /// Legal size (8.5 x 14 inches).
    /// </summary>
    NorthAmericaLegal = 2,
    
    /// <summary>
    /// A4 size (210 x 297 mm).
    /// </summary>
    IsoA4 = 3,
    
    /// <summary>
    /// A3 size (297 x 420 mm).
    /// </summary>
    IsoA3 = 4,
    
    /// <summary>
    /// A5 size (148 x 210 mm).
    /// </summary>
    IsoA5 = 5,
    
    /// <summary>
    /// Tabloid size (11 x 17 inches).
    /// </summary>
    NorthAmericaTabloid = 6,
    
    /// <summary>
    /// Executive size (7.25 x 10.5 inches).
    /// </summary>
    NorthAmericaExecutive = 7,
    
    /// <summary>
    /// B4 size (250 x 353 mm).
    /// </summary>
    JisB4 = 8,
    
    /// <summary>
    /// B5 size (176 x 250 mm).
    /// </summary>
    JisB5 = 9
}