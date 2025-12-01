using System;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Bonfires
{
    /// <summary>
    /// This class is the Block Entity for the bonfire. It manages the bonfire's internal state,
    /// including its fuel, burning duration, and the logic that executes while it's burning.
    /// </summary>
    public class BlockEntityBonfire : BlockEntity, IHeatSource
    {
        // --- Constants for gameplay balance and clarity ---
        private const int FIREWOOD_BURNTIME_MULTIPLIER = 4;
        private const int FALLBACK_FIREWOOD_BURNTIME = 24; // Used if firewood properties can't be found.
        private const int HEAT_STRENGTH_BURNING = 30;
        private const int HEAT_STRENGTH_EXTINCT = 1;
        private const float AMBIENT_SOUND_VOLUME = 2f;
        private const float FIRE_DAMAGE = 2f;
        private const double IGNITE_CHANCE = 0.125;
        private const double FIRE_SPREAD_CHANCE = 0.2;
        private const int ENTITY_DETECTION_RANGE = 3;
        private const int FIRE_SPREAD_UP_MIN = 2;
        private const int FIRE_SPREAD_UP_MAX = 5;
        public const int MAX_FUEL = 32; // Maximum fuel the bonfire can hold.

        // --- Private fields for managing state ---
        private float _remainingBurnSeconds;
        private float _secondsPerFuelItem;
        private Block? _fireBlock; // The block used for fire spreading.
        public string StartedByPlayerUid = null!;
        private ILoadedSound? _ambientSound;
        private long _listener; // ID for the game tick listener.

        // A pre-calculated cuboid for checking entity collisions with the fire.
        private static readonly Cuboidf FireCuboid = new(-0.35f, 0, -0.35f, 1.35f, 2.8f, 1.35f);

        /// <summary>
        /// Returns true if the bonfire's current state is "lit".
        /// </summary>
        public bool Burning => Block.LastCodePart().Equals("lit");

        /// <summary>
        /// Calculates the current amount of fuel based on remaining burn time.
        /// </summary>
        public int TotalFuel => _secondsPerFuelItem > 0 ? (int)Math.Ceiling(_remainingBurnSeconds / _secondsPerFuelItem) : 0;

        /// <summary>
        /// Called when the block entity is initialized. Sets up essential properties like burn time and fire block.
        /// </summary>
        public override void Initialize(ICoreAPI api)
        {
            base.Initialize(api);

            // Determine the burn duration for a single piece of fuel.
            var firewood = Api.World.GetItem(new AssetLocation("firewood"));
            if (firewood?.CombustibleProps != null)
            {
                _secondsPerFuelItem = firewood.CombustibleProps.BurnDuration * FIREWOOD_BURNTIME_MULTIPLIER;
            }
            else
            {
                _secondsPerFuelItem = FALLBACK_FIREWOOD_BURNTIME * FIREWOOD_BURNTIME_MULTIPLIER;
            }

            // Cache the fire block for spreading mechanics.
            _fireBlock = Api.World.GetBlock(new AssetLocation("fire"));
            if (_fireBlock == null)
            {
                Api.World.Logger.Warning("Bonfires mod could not find block with code 'fire'. Fire spreading from bonfires will be disabled.");
            }

            // If the bonfire is already burning (e.g., chunk loaded), start its ticking logic.
            if (Burning)
            {
                InitSoundsAndTicking();
            }
        }

        /// <summary>
        /// Sets up the game tick listener and ambient sound when the fire is active.
        /// </summary>
        private void InitSoundsAndTicking()
        {
            _listener = RegisterGameTickListener(OnceASecond, 1000); // Register a 1-second tick.
            if (_ambientSound == null && Api.Side == EnumAppSide.Client)
            {
                _ambientSound = ((IClientWorldAccessor)Api.World).LoadSound(new SoundParams
                {
                    Location = new AssetLocation("vintagebonfires:sounds/bonfire.ogg"),
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
        /// This method is called once per second while the bonfire is burning.
        /// It handles fuel consumption, entity damage, and fire spreading.
        /// </summary>
        private void OnceASecond(float dt)
        {
            // Client-side: Manage sound fading.
            if (Api.Side == EnumAppSide.Client)
            {
                if (!Burning)
                {
                    _ambientSound?.FadeOutAndStop(1);
                }
                return;
            }

            // Server-side: Core burning logic.
            if (Burning)
            {
                int oldFuel = TotalFuel;
                _remainingBurnSeconds -= dt;

                if (oldFuel != TotalFuel)
                {
                    MarkDirty(true); // Sync changes with clients.
                }

                // If fuel runs out, extinguish the fire.
                if (_remainingBurnSeconds <= 0)
                {
                    KillFire();
                    return;
                }

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
                    KillFire();
                    return;
                }

                // --- Spread Fire ---
                if (((ICoreServerAPI)Api).Server.Config.AllowFireSpread && FIRE_SPREAD_CHANCE > Api.World.Rand.NextDouble())
                {
                    TrySpreadFireAllDirs();
                }
            }
        }

        /// <summary>
        /// Changes the bonfire's block state to a new one (e.g., "lit", "extinct").
        /// </summary>
        public void SetBlockState(string state)
        {
            AssetLocation loc = Block.CodeWithVariant("burnstate", state);
            Block block = Api.World.GetBlock(loc);
            if (block == null) return;

            Api.World.BlockAccessor.ExchangeBlock(block.Id, Pos);
            this.Block = block;
        }

        /// <summary>
        /// Ignites the bonfire, changing its state to "lit" and starting the burning process.
        /// </summary>
        public void Ignite(string playerUid)
        {
            if (TotalFuel <= 0) return; // Can only ignite if there is fuel.

            StartedByPlayerUid = playerUid;
            SetBlockState("lit");
            MarkDirty(true);
            InitSoundsAndTicking();
        }

        /// <summary>
        /// Extinguishes the fire, changing its state to "extinct" and stopping the burning process.
        /// Also handles cracking or breaking nearby ore and rock.
        /// </summary>
        public void KillFire()
        {
            _remainingBurnSeconds = 0;
            UnregisterGameTickListener(_listener);

            // --- Crack or Break Ore/Rock Logic ---
            foreach (BlockFacing facing in BlockFacing.ALLFACES)
            {
                BlockPos npos = Pos.AddCopy(facing);
                Block? targetBlock = Api.World.BlockAccessor.GetBlock(npos);
                if (targetBlock == null || targetBlock.BlockId == 0) continue;

                string blockCode = targetBlock.FirstCodePart();

                // If the block is already cracked, break it into its drops.
                if (blockCode.Equals("cracked_ore") || blockCode.Equals("crackedrock"))
                {
                    Api.World.BlockAccessor.BreakBlock(npos, null);
                }
                // If it's ore, turn it into cracked ore.
                else if (blockCode.Equals("ore"))
                {
                    AssetLocation crackedLoc = targetBlock.CodeWithPart("cracked_ore");
                    crackedLoc.Domain = "vintagebonfires";
                    Block crackedBlock = Api.World.GetBlock(crackedLoc);
                    if (crackedBlock != null)
                    {
                        Api.World.BlockAccessor.ExchangeBlock(crackedBlock.Id, npos);
                    }
                }
                // If it's rock, turn it into vanilla cracked rock.
                else if (blockCode.Equals("rock"))
                {
                    AssetLocation crackedLoc = targetBlock.CodeWithPart("crackedrock");
                    crackedLoc.Domain = "game";
                    Block crackedBlock = Api.World.GetBlock(crackedLoc);
                    if (crackedBlock != null)
                    {
                        Api.World.BlockAccessor.ExchangeBlock(crackedBlock.Id, npos);
                    }
                }
            }
            
            SetBlockState("extinct");
        }

        /// <summary>
        /// Part of the IHeatSource interface. Provides heat to the surroundings when burning.
        /// </summary>
        public float GetHeatStrength(IWorldAccessor world, BlockPos heatSourcePos, BlockPos heatReceiverPos)
        {
            return Burning ? HEAT_STRENGTH_BURNING : HEAT_STRENGTH_EXTINCT;
        }

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
            if (block.Replaceable < 6000) return false;

            BlockEntity be = Api.World.BlockAccessor.GetBlockEntity(pos);
            if (be?.GetBehavior<BEBehaviorBurning>() != null) return false;

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
        /// Loads the bonfire's state from saved world data.
        /// </summary>
        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
        {
            base.FromTreeAttributes(tree, worldForResolving);
            _remainingBurnSeconds = tree.GetFloat("remainingBurnSeconds");
        }

        /// <summary>
        /// Saves the bonfire's state to world data.
        /// </summary>
        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetFloat("remainingBurnSeconds", _remainingBurnSeconds);
        }



        /// <summary>
        /// Called when the block is removed. Cleans up sounds and other resources.
        /// </summary>
        public override void OnBlockRemoved()
        {
            base.OnBlockRemoved();

            if (_ambientSound != null)
            {
                _ambientSound.Stop();
                _ambientSound.Dispose();
                _ambientSound = null;
            }

            // Drop remaining fuel when the block is broken.
            if (Api.Side == EnumAppSide.Server && TotalFuel > 0)
            {
                var firewoodStack = new ItemStack(Api.World.GetItem(new AssetLocation("firewood")), TotalFuel);
                Api.World.SpawnItemEntity(firewoodStack, Pos.ToVec3d().Add(0.5, 0.5, 0.5));
            }
        }

        /// <summary>
        /// Provides information for the block info HUD (e.g., "Fuel: 5/32").
        /// </summary>
        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
        {
            base.GetBlockInfo(forPlayer, dsc);
            dsc.AppendLine(Lang.Get("vintagebonfires:bonfire-fuel", TotalFuel, MAX_FUEL));
        }

        /// <summary>
        /// Adds a specified amount of fuel to the bonfire.
        /// </summary>
        /// <returns>True if fuel was successfully added, false otherwise.</returns>
        public bool Refuel(int amount)
        {
            if (TotalFuel >= MAX_FUEL) return false;

            _remainingBurnSeconds += amount * _secondsPerFuelItem;
            if (TotalFuel > MAX_FUEL)
            {
                _remainingBurnSeconds = MAX_FUEL * _secondsPerFuelItem;
            }

            MarkDirty(true);
            return true;
        }
    }
}