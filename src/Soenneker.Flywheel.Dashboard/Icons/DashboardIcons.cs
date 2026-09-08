using Soenneker.Lucide.Enums.Icons;

namespace Soenneker.Flywheel.Dashboard.Icons;

/// <summary>Declares icons used internally by Quark controls so the SVG generator includes them.</summary>
internal static class DashboardIcons
{
    /// <summary>Icons resolved dynamically by password fields, pagination, and input controls.</summary>
    internal static readonly LucideIcon[] Required =
    [
        LucideIcon.Eye, LucideIcon.EyeOff, LucideIcon.ChevronLeft, LucideIcon.ChevronRight,
        LucideIcon.ChevronsLeft, LucideIcon.ChevronsRight, LucideIcon.Check, LucideIcon.X,
        LucideIcon.Search, LucideIcon.LoaderCircle
    ];
}
