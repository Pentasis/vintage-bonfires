using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Bonfires;

public class BlockEntityCampfire : BlockEntityFirepit
{
    // --- Constants for gameplay balance and clarity (adapted from BEBonfire) ---
    private const int HEAT_STRENGTH_BURNING = 40; // Increased heat compared to vanilla firepit (10)
    private const int HEAT_STRENGTH_EXTINCT = 1;
    private const float AMBIENT_SOUND_VOLUME = 2f; // Re-using bonfire sound volume
    private const float FIRE_DAMAGE = 2f;
    private const double IGNITE_CHANCE = 0.125;
    private const double FIRE_SPREAD_CHANCE = 0.2;
    private const int ENTITY_DETECTION_RANGE = 3;
    private const int FIRE_SPREAD_UP_MIN = 2;
    private const int FIRE_SPREAD_UP_MAX = 5;

    // --- Private fields for managing state (adapted from BEBonfire) ---
    private Block? _fireBlock; // The block used for fire spreading.
    public string StartedByPlayerUid = null!; // Player who ignited the campfire, used for claim checks
    private ILoadedSound? _ambientSound;
    private long _fireSpreadListener; // ID for the fire spread game tick listener.

    // A pre-calculated cuboid for checking entity collisions with the fire.
    private static readonly Cuboidf FireCuboid = new(-0.35f, 0, -0.35f, 1.35f, 2.8f, 1.35f);

    /// <summary>
    /// Initializes the block entity. Caches the fire block and sets up sounds/ticking if already burning.
    /// </summary>
    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);

        // Enforce firewood only in the fuel slot
        Inventory.SlotModified += OnSlotModified;

        // Cache the fire block for spreading mechanics.
        _fireBlock = Api.World.GetBlock(new AssetLocation("fire"));
        if (_fireBlock == null)
        {
            Api.World.Logger.Warning("Bonfires mod could not find block with code 'fire'. Fire spreading from campfires will be disabled.");
        }

        // If the campfire is already burning (e.g., chunk loaded), start its ticking logic.
        if (IsBurning)
        {
            InitSoundsAndFireSpreading();
        }
    }

    /// <summary>
    /// Event handler to enforce that only firewood can be placed in the fuel slot.
    /// </summary>
    private void OnSlotModified(int slotId)
    {
        // Slot 0 is the fuel slot. Only run this check on the server to spawn items.
        if (Api.Side == EnumAppSide.Server && slotId == 0)
        {
            ItemStack stack = Inventory[0].Itemstack;
            // If there is an item and it is NOT firewood
            if (stack != null && stack.Collectible.Code.Path != "firewood")
            {
                // Eject the invalid item into the world
                Api.World.SpawnItemEntity(stack, Pos.ToVec3d().Add(0.5, 0.5, 0.5));
                
                // Clear the slot
                Inventory[0].Itemstack = null;
                Inventory[0].MarkDirty();
            }
        }
    }

    /// <summary>
    /// Sets up the fire spreading tick listener and ambient sound when the fire is active.
    /// </summary>
    private void InitSoundsAndFireSpreading()
    {
        // Register a 1-second tick for fire spreading and entity damage
        // Ensure we don't register multiple listeners
        if (_fireSpreadListener == 0)
        {
            _fireSpreadListener = RegisterGameTickListener(OnFireSpreadTick, 1000);
        }

        if (_ambientSound == null && Api.Side == EnumAppSide.Client)
        {
            _ambientSound = ((IClientWorldAccessor)Api.World).LoadSound(new SoundParams
            {
                Location = new AssetLocation("vintagebonfires:sounds/bonfire.ogg"), // Re-use bonfire sound
                ShouldLoop = true,
                Position = Pos.ToVec3f().Add(0.5f, 0.25f, 0.5f),
                DisposeOnFinish = false,
                Volume = AMBIENT_SOUND_VOLUME
            });

            if (_ambientSound != null)
            {
                _ambientSound.PlaybackPosition = _ambientSound.SoundLengthSeconds * (float)Api.World.Rand.NextDouble();
                _ambientSound.Start();
            }
        }
    }

    /// <summary>
    /// This method is called once per second while the campfire is burning for fire spreading and entity damage.
    /// </summary>
    private void OnFireSpreadTick(float dt)
    {
        // Check if burning state has changed (since we don't hook into OnBurnTick directly)
        if (!IsBurning)
        {
            // Cleanup if it stopped burning
            if (_fireSpreadListener != 0)
            {
                UnregisterGameTickListener(_fireSpreadListener);
                _fireSpreadListener = 0;
            }
            if (Api.Side == EnumAppSide.Client)
            {
                _ambientSound?.FadeOutAndStop(1);
                _ambientSound?.Dispose();
                _ambientSound = null;
            }
            return;
        }

        // Client-side: Just ensure sound is playing
        if (Api.Side == EnumAppSide.Client)
        {
            if (_ambientSound != null && !_ambientSound.IsPlaying)
            {
                _ambientSound.Start();
            }
            return;
        }

        // Server-side: Core fire spreading and entity damage logic.
        
        // --- Damage and Ignite Entities ---
        Entity[] entities = Api.World.GetEntitiesAround(Pos.ToVec3d().Add(0.5, 0.5, 0.5), ENTITY_DETECTION_RANGE, ENTITY_DETECTION_RANGE, _ => true);
        Vec3d ownPos = Pos.ToVec3d();
        foreach (Entity entity in entities)
        {
            if (!CollisionTester.AabbIntersect(entity.CollisionBox, entity.ServerPos.X, entity.ServerPos.Y, entity.ServerPos.Z, FireCuboid, ownPos)) continue;

            if (entity.Alive)
            {
                entity.ReceiveDamage(new DamageSource { Source = EnumDamageSource.Block, SourceBlock = _fireBlock, SourcePos = ownPos, Type = EnumDamageType.Fire }, FIRE_DAMAGE);
            }

            if (Api.World.Rand.NextDouble() < IGNITE_CHANCE)
            {
                entity.Ignite();
            }
        }

        // Extinguish if in water.
        if (Api.World.BlockAccessor.GetBlock(Pos).LiquidCode == "water")
        {
            // Set block state to extinct. This mimics KillFire but uses the base class's method if available, 
            // or just sets the block state which the base class will pick up on its next tick.
            // BlockEntityFirepit uses setBlockState("extinct") internally. We can't access it if it's private.
            // But we can use the public method from BlockEntityBonfire... oh wait, we are in BlockEntityCampfire now.
            // We can use the helper method below.
            SetBlockState("extinct");
            
            // We also need to stop the fuel burn.
            // fuelBurnTime is public in BlockEntityFirepit.
            fuelBurnTime = 0;
            maxFuelBurnTime = 0;
            
            MarkDirty(true);
            return;
        }

        // --- Spread Fire ---
        if (((ICoreServerAPI)Api).Server.Config.AllowFireSpread && FIRE_SPREAD_CHANCE > Api.World.Rand.NextDouble())
        {
            TrySpreadFireAllDirs();
        }
    }

    /// <summary>
    /// Helper to set block state, similar to BlockEntityFirepit's internal method.
    /// </summary>
    private void SetBlockState(string state)
    {
        AssetLocation loc = Block.CodeWithVariant("burnstate", state);
        Block block = Api.World.GetBlock(loc);
        if (block == null) return;

        Api.World.BlockAccessor.ExchangeBlock(block.Id, Pos);
        this.Block = block;
    }

    /// <summary>
    /// Overrides GetHeatStrength for increased heat output from the campfire.
    /// </summary>
    public new float GetHeatStrength(IWorldAccessor world, BlockPos heatSourcePos, BlockPos heatReceiverPos)
    {
        return IsBurning ? HEAT_STRENGTH_BURNING : HEAT_STRENGTH_EXTINCT;
    }

    /// <summary>
    /// Overrides igniteFuel. While we can't stop the base class from calling its own version,
    /// we can use this to ensure any external calls respect our logic.
    /// </summary>
    public new void igniteFuel()
    {
        base.igniteFuel();
    }

    // --- Fire Spreading Logic (adapted from BEBonfire) ---

    /// <summary>
    /// Attempts to spread fire to adjacent and upward blocks.
    /// </summary>
    private void TrySpreadFireAllDirs()
    {
        foreach (BlockFacing facing in BlockFacing.ALLFACES)
        {
            BlockPos npos = Pos.AddCopy(facing);
            TrySpreadTo(npos);
        }
        for (int up = FIRE_SPREAD_UP_MIN; up <= FIRE_SPREAD_UP_MAX; up++)
        {
            BlockPos npos = Pos.UpCopy(up);
            TrySpreadTo(npos);
        }
    }

    /// <summary>
    /// Logic for spreading fire to a single block position.
    /// </summary>
    public bool TrySpreadTo(BlockPos pos)
    {
        if (_fireBlock == null) return false;

        var block = Api.World.BlockAccessor.GetBlock(pos);
        if (block.Replaceable < 6000) return false; // Not easily replaceable

        BlockEntity be = Api.World.BlockAccessor.GetBlockEntity(pos);
        if (be?.GetBehavior<BEBehaviorBurning>() != null) return false; // Already burning

        // Fire needs a combustible block nearby to be placed.
        BlockPos? fuelPos = null;
        foreach (BlockFacing firefacing in BlockFacing.ALLFACES)
        {
            var npos = pos.AddCopy(firefacing);
            if (CanBurn(npos) && Api.World.BlockAccessor.GetBlockEntity(npos)?.GetBehavior<BEBehaviorBurning>() == null)
            {
                    fuelPos = npos;
                    break;
            }
        }
        if (fuelPos == null) return false;

        // Respect land claims.
        // StartedByPlayerUid needs to be set when the campfire is ignited.
        IPlayer? player = Api.World.PlayerByUid(StartedByPlayerUid);
        if (player != null && Api.World.Claims.TestAccess(player, pos, EnumBlockAccessFlags.BuildOrBreak) != EnumWorldAccessResponse.Granted)
        {
            return false;
        }

        Api.World.BlockAccessor.SetBlock(_fireBlock.BlockId, pos);

        BlockEntity? befire = Api.World.BlockAccessor.GetBlockEntity(pos);
        if (befire != null)
        {
            befire.GetBehavior<BEBehaviorBurning>()?.OnFirePlaced(pos, fuelPos, StartedByPlayerUid);
        }

        return true;
    }

    /// <summary>
    /// Checks if a block at a given position is combustible and not reinforced.
    /// </summary>
    private bool CanBurn(BlockPos pos)
    {
        return OnCanBurn(pos) && Api.ModLoader.GetModSystem<ModSystemBlockReinforcement>()?.IsReinforced(pos) != true;
    }

    /// <summary>
    /// Helper method to check if a block has combustible properties.
    /// </summary>
    private bool OnCanBurn(BlockPos pos)
    {
        Block block = Api.World.BlockAccessor.GetBlock(pos);
        return block.CombustibleProps is { BurnDuration: > 0 };
    }

    /// <summary>
    /// Loads the campfire's state from saved world data, including the player who started it.
    /// </summary>
    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
    {
        base.FromTreeAttributes(tree, worldForResolving);
        StartedByPlayerUid = tree.GetString("startedByPlayerUid", null);
        // Re-initialize fire spreading listener if burning after loading
        if (IsBurning && _fireSpreadListener == 0)
        {
            InitSoundsAndFireSpreading();
        }
    }

    /// <summary>
    /// Saves the campfire's state to world data, including the player who started it.
    /// </summary>
    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        tree.SetString("startedByPlayerUid", StartedByPlayerUid);
    }

    /// <summary>
    /// Called when the block is removed. Cleans up sounds and other resources.
    /// </summary>
    public override void OnBlockRemoved()
    {
        base.OnBlockRemoved();

        // Clean up fire spread listener and ambient sound
        if (_fireSpreadListener != 0)
        {
            UnregisterGameTickListener(_fireSpreadListener);
            _fireSpreadListener = 0;
        }
        if (_ambientSound != null)
        {
            _ambientSound.Stop();
            _ambientSound.Dispose();
            _ambientSound = null;
        }
    }
}