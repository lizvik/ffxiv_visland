using System.ComponentModel;
using visland.Helpers;

namespace visland.Granary;

public class GranaryConfig : Configuration.Node {
    public enum UpdateStrategy {
        [Description(LocalizationKeys.Common.Manual)]
        Manual,

        [Description(LocalizationKeys.Granary.ExtendSameDestination)]
        MaxCurrent,

        [Description(LocalizationKeys.Granary.ReassignToTwoLowestStocks)]
        BestDifferent,

        [Description(LocalizationKeys.Granary.ReassignBothToLowestStock)]
        BestSame,
    }

    public CollectStrategy Collect = CollectStrategy.Manual;
    public UpdateStrategy Reassign = UpdateStrategy.Manual;
}
