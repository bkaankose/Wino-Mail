namespace Wino.Core.Domain.Enums;

/// <summary>
/// Print duplex (double-sided) options.
/// </summary>
public enum PrintDuplex
{
    /// <summary>
    /// Default duplex mode.
    /// </summary>
    Default = 0,
    
    /// <summary>
    /// Single-sided printing.
    /// </summary>
    Simplex = 1,
    
    /// <summary>
    /// Double-sided printing with pages flipped horizontally.
    /// </summary>
    DuplexShortEdge = 2,
    
    /// <summary>
    /// Double-sided printing with pages flipped vertically.
    /// </summary>
    DuplexLongEdge = 3
}