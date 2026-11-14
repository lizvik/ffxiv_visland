using visland.Helpers;
using System.ComponentModel;

namespace visland;

public enum CollectStrategy {
    [Description(LocalizationKeys.Common.Manual)]
    Manual,

    [Description(LocalizationKeys.Common.CollectWithoutOvercap)]
    NoOvercap,

    [Description(LocalizationKeys.Common.CollectAllowOvercap)]
    FullAuto,
}
