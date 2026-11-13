using System.Collections.Generic;

namespace visland.Gathering;

internal static class IslandNodeMaterials {
    // EObjName IDs and Item IDs, independent of the client's display language.
    // Yield reference: https://www.thonky.com/final-fantasy-xiv/island-sanctuary-gathering-nodes
    // IDs verified against MJIGatheringObject and MJIGatheringItem game sheets.
    internal static readonly IReadOnlyDictionary<uint, uint[]> ByObjectId = new Dictionary<uint, uint[]> {
        [2013171] = [37586], // lightly gnawed pumpkin
        [2013172] = [37584], // partially consumed cabbage
        [2013173] = [37560, 37553, 39224], // tualong tree
        [2013174] = [37561, 37551, 39225], // palm tree
        [2013175] = [37552, 37562, 39226], // island apple tree
        [2013176] = [37560, 37563, 39227], // mahogany tree
        [2013177] = [37564, 37554, 39891], // bluish rock
        [2013178] = [37554, 39890, 37565], // smooth white rock
        [2013179] = [37566, 37554], // crystal-banded rock
        [2013180] = [37570, 37559], // mound of dirt
        [2013181] = [37558, 37583], // wild popoto
        [2013182] = [37591, 37558], // wild parsnip
        [2013183] = [37559, 37571], // submerged sand
        [2013184] = [37567, 37562], // sugarcane
        [2013185] = [37568, 37558], // cotton plant
        [2013186] = [37558, 37569], // agave plant
        [2013187] = [37575, 37555], // large shell
        [2013188] = [37556, 37576], // seaweed tangle
        [2013189] = [37557, 37577], // coral formation
        [2013190] = [37572, 41630, 37554], // rough black rock
        [2013191] = [37554, 37573], // quartz formation
        [2013192] = [37554, 37574], // speckled rock
        [2013300] = [39228], // multicolored isleblooms
        [2013375] = [39889], // glowing fungus
        [2013376] = [39888, 39887], // composite rock
        [2013377] = [39892, 39893], // stalagmite
        [2013523] = [41632, 41631], // yellowish rock
        [2013524] = [41633, 41634], // island crystal cluster
    };
}
