using Vintagestory.API.Common;

namespace BonFires
{
    public class BonFiresModSystem : ModSystem
    {
        public override void Start(ICoreAPI api)
        {
            base.Start(api);
            api.RegisterBlockEntityClass("BlockEntityBonfire", typeof(BlockEntityBonfire));
            api.RegisterBlockClass("BlockBonfire", typeof(BlockBonfire));
        }
    }
}