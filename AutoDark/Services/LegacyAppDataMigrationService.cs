using AutoDark.Core.Services;

namespace AutoDark.Services;

public static class LegacyAppDataMigrationService
{
    public static void MigrateAndCleanUp()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(appData))
            {
                return;
            }

            var legacyDirectory = Path.Combine(appData, "AutoDark");
            if (!Directory.Exists(legacyDirectory))
            {
                return;
            }

            // The legacy directory is only removed once its settings are safe
            // at the new location (or there was nothing usable to migrate).
            if (TryMigrateSettings(Path.Combine(legacyDirectory, "settings.json")))
            {
                Directory.Delete(legacyDirectory, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static bool TryMigrateSettings(string legacySettingsPath)
    {
        if (File.Exists(SettingsStore.DefaultPath) || !File.Exists(legacySettingsPath))
        {
            return true;
        }

        var migrated = LegacySettingsMigrator.TryMigrate(File.ReadAllText(legacySettingsPath));
        if (migrated is null)
        {
            // Unreadable legacy settings: keep the directory so nothing is
            // destroyed that could still be recovered by hand.
            return false;
        }

        new SettingsStore(SettingsStore.DefaultPath).Save(migrated);
        return true;
    }
}
