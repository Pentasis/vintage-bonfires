using Vintagestory.API.Common;

namespace Bonfires
{
	public class BonfiresMod : ModSystem
	{
		public override void Start(ICoreAPI api)
		{
			api.RegisterBlockEntityClass("BlockEntityBonfire", typeof(BlockEntityBonfire));
			api.RegisterBlockClass("BlockBonfire", typeof(BlockBonfire));
		}
	}
}
