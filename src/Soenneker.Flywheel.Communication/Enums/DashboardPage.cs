using Soenneker.Gen.EnumValues;

namespace Soenneker.Flywheel.Communication.Enums;

/// <summary>Identifies a page handled by the Flywheel dashboard router.</summary>
[EnumValue]
public readonly partial struct DashboardPage
{
    public static readonly DashboardPage NotFound = new(0);
    public static readonly DashboardPage Dashboard = new(1);
    public static readonly DashboardPage SignIn = new(2);
    public static readonly DashboardPage Recurring = new(3);
    public static readonly DashboardPage Scheduled = new(4);
    public static readonly DashboardPage Jobs = new(5);
    public static readonly DashboardPage Servers = new(6);
    public static readonly DashboardPage ServerDetails = new(7);
    public static readonly DashboardPage Schedule = new(8);
}
