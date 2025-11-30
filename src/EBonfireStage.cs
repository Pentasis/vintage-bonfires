namespace Bonfires
{
    // Enum to represent the distinct stages of a bonfire, improving code readability.
    public enum EBonfireStage
    {
        Base,       // 0: The initial base structure
        Construct1, // 1: First stage of construction
        Construct2, // 2: Second stage of construction
        Unlit,      // 3: Fully constructed and fueled, ready to be lit
        Lit,        // 4: Actively burning
        Extinct     // 5: Burned out, needs to be rebuilt
    }
}
