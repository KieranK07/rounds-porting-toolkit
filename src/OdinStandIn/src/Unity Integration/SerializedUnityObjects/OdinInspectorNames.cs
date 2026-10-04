// The Sirenix.Serialization.dll that shipped with older ROUNDS builds came from Odin Inspector, which also put these
// base classes in it under Sirenix.OdinInspector. Mods built against it (WillsWackyManagers) derive from them.
// They behave like the open-source classes they extend.

namespace Sirenix.OdinInspector
{
    public abstract class SerializedMonoBehaviour : Sirenix.Serialization.SerializedMonoBehaviour { }

    public abstract class SerializedScriptableObject : Sirenix.Serialization.SerializedScriptableObject { }

    public abstract class SerializedBehaviour : Sirenix.Serialization.SerializedBehaviour { }

    public abstract class SerializedComponent : Sirenix.Serialization.SerializedComponent { }

    public abstract class SerializedStateMachineBehaviour : Sirenix.Serialization.SerializedStateMachineBehaviour { }
}
