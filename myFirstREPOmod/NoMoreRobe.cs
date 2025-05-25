using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using System.Reflection;

namespace NoMoreRobe;

[BepInPlugin("Vorshim92.NoMoreRobe", "NoMoreRobe", "1.0.0")]
public class NoMoreRobe : BaseUnityPlugin
{
    internal static NoMoreRobe Instance { get; private set; } = null!;
    internal new static ManualLogSource Logger => Instance._logger;
    private ManualLogSource _logger => base.Logger;
    internal Harmony? Harmony { get; set; }

    // Configurazione
    private ConfigEntry<bool> _enableDeathFix = null!;
    private ConfigEntry<bool> _forceLootDrop = null!;
    private ConfigEntry<bool> _debugLogging = null!;

    private void Awake()
    {
        Instance = this;
        
        // Setup configurazione
        _enableDeathFix = Config.Bind("General", "EnableDeathFix", true, "Abilita il fix per il bug di morte del Robe");
        _forceLootDrop = Config.Bind("General", "ForceLootDrop", false, "Forza sempre il drop del loot quando il Robe muore");
        _debugLogging = Config.Bind("Debug", "EnableDebugLogging", false, "Abilita log di debug dettagliati");
        
        // Prevent the plugin from being deleted
        this.gameObject.transform.parent = null;
        this.gameObject.hideFlags = HideFlags.HideAndDontSave;

        Patch();

        Logger.LogInfo($"{Info.Metadata.GUID} v{Info.Metadata.Version} has loaded!");
    }

    internal void Patch()
    {
        Harmony ??= new Harmony(Info.Metadata.GUID);
        Harmony.PatchAll();
        Logger.LogInfo("Patches applied successfully!");
    }

    internal void Unpatch()
    {
        Harmony?.UnpatchSelf();
    }

    public static bool ShouldEnableDeathFix() => Instance._enableDeathFix.Value;
    public static bool ShouldForceLootDrop() => Instance._forceLootDrop.Value;
    public static bool IsDebugLoggingEnabled() => Instance._debugLogging.Value;
}

// Assicuriamoci che il processo di morte venga completato
[HarmonyPatch(typeof(EnemyHealth), "Update")]
public class EnemyHealthDeathFix
{
    static void Postfix(EnemyHealth __instance)
    {
        if (!NoMoreRobe.ShouldEnableDeathFix()) return;
        
        // Usa reflection per accedere ai campi privati
        var deadImpulse = ReflectionHelpers.GetPrivateField<bool>(__instance, "deadImpulse");
        var deadImpulseTimer = ReflectionHelpers.GetPrivateField<float>(__instance, "deadImpulseTimer");
        
        // Se il nemico è morto ma deadImpulse è ancora attivo dopo troppo tempo
        if (__instance.dead == false && deadImpulse && deadImpulseTimer < -5f)
        {
            NoMoreRobe.Logger.LogWarning($"Forcing death completion for stuck enemy");
            __instance.DeathImpulseRPC();
        }
    }
}

// Forziamo il drop del loot per il Robe quando muore (se abilitato in confi, di base funziona, quindi non attiviamolo)
[HarmonyPatch(typeof(EnemyParent), "Despawn")]
public class RobeLootFix
{
    static void Prefix(EnemyParent __instance)
    {
        if (!NoMoreRobe.ShouldForceLootDrop()) return;
        
        // Controlla se è un Robe e se è morto
        if (__instance.enemyName.Contains("Robe") && 
            __instance.Enemy.HasHealth && 
            __instance.Enemy.Health.healthCurrent <= 0)
        {
            // Forza il timer per garantire il drop del loot
            if (NoMoreRobe.IsDebugLoggingEnabled())
                NoMoreRobe.Logger.LogDebug($"Forcing loot timer for dying Robe");
            
            // Usa reflection per accedere al campo privato
            ReflectionHelpers.SetPrivateField(__instance, "valuableSpawnTimer", 10f);
        }
    }
}

// Assicuriamoci che OnDeath venga chiamato correttamente per il Robe
[HarmonyPatch(typeof(EnemyRobe), "Update")]
public class RobeDeathStateFix
{
    static void Postfix(EnemyRobe __instance)
    {
        if (!NoMoreRobe.ShouldEnableDeathFix()) return;
        
        // Se il Robe è morto ma non in stato Despawn, forzalo
        if (__instance.enemy.HasHealth && 
            __instance.enemy.Health.dead && 
            __instance.currentState != EnemyRobe.State.Despawn)
        {
            NoMoreRobe.Logger.LogWarning($"Forcing Despawn state for dead Robe");
            __instance.UpdateState(EnemyRobe.State.Despawn);
            __instance.enemy.EnemyParent.SpawnedTimerSet(0f);
        }
    }
}

// Previeniamo che il Robe rimanga "bloccato" dopo la morte
[HarmonyPatch(typeof(EnemyHealth), "DeathImpulseRPC")]
public class EnsureProperDeathState
{
    static void Postfix(EnemyHealth __instance)
    {
        if (!NoMoreRobe.ShouldEnableDeathFix()) return;
        
        var enemy = __instance.GetComponent<Enemy>();
        if (enemy && enemy.EnemyParent.enemyName.Contains("Robe"))
        {
            // Assicura che tutti i componenti siano disabilitati
            if (NoMoreRobe.IsDebugLoggingEnabled())
                NoMoreRobe.Logger.LogDebug($"Ensuring proper death state for Robe");
            
            // Forza il cambio di stato
            enemy.CurrentState = EnemyState.Despawn;
            
            // Se ha un EnemyRobe component, aggiorna anche quello
            var enemyRobe = enemy.GetComponent<EnemyRobe>();
            if (enemyRobe)
            {
                enemyRobe.UpdateState(EnemyRobe.State.Despawn);
            }
        }
    }
}

// Monitoriamo lo stato del Robe per debug
[HarmonyPatch(typeof(EnemyRobe), "OnDeath")]
public class RobeDeathMonitor
{
    static void Postfix(EnemyRobe __instance)
    {
        if (NoMoreRobe.IsDebugLoggingEnabled())
        {
            NoMoreRobe.Logger.LogInfo($"Robe OnDeath called - Current State: {__instance.currentState}");
            NoMoreRobe.Logger.LogInfo($"Health: {__instance.enemy.Health.healthCurrent}/{__instance.enemy.Health.health}");
            NoMoreRobe.Logger.LogInfo($"Is Dead: {__instance.enemy.Health.dead}");
        }
    }
}

public static class ReflectionHelpers
{
    public static void SetPrivateField(object instance, string fieldName, object value)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        if (field != null)
        {
            field.SetValue(instance, value);
        }
        else if (NoMoreRobe.IsDebugLoggingEnabled())
        {
            NoMoreRobe.Logger.LogWarning($"Field {fieldName} not found in {instance.GetType().Name}");
        }
    }
    
    public static T? GetPrivateField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        if (field != null)
        {
            return (T)field.GetValue(instance);
        }
        
        if (NoMoreRobe.IsDebugLoggingEnabled())
        {
            NoMoreRobe.Logger.LogWarning($"Field {fieldName} not found in {instance.GetType().Name}");
        }
        return default(T);
    }
}