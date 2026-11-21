// Run on Windows with the .NET 10 SDK: dotnet run --file tests/DriveWear.cs
#:property TargetFramework=net10.0-windows10.0.19041.0
#:property UseWPF=true
#:property PublishAot=false
#:property AssemblyName=Rigsight.Tests
#:project ../src/Rigsight.Agent/Rigsight.Agent.csproj
#:project ../src/Rigsight/Rigsight.csproj

using System.IO;
using Microsoft.Data.Sqlite;
using Rigsight.Agent.Sensors;
using Rigsight.Core.Data;
using Rigsight.Core.Protocol;
using Rigsight.Models;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

Check(SensorHost.ReportedLife(0, false) is null, "An empty SMART snapshot cannot supply wear.");
Check(SensorHost.ReportedLife(null, true) is null, "Other attributes cannot replace a missing wear reading.");
Check(SensorHost.ReportedLife(0, true) == 0, "A measured zero must survive collection.");
Check(SensorHost.ReportedLife(90, true) == 90, "Ordinary wear must survive collection.");

var usb = new DriveHealthInfo { Key = "usb", Name = "USB SSD", Bus = "BusTypeUsb", IsSsd = true };
var group = new DriveGroup("usb", usb.Name, usb, null, [], "other");
Check(group.IsUsb && group.LifeText == "Wear unavailable over USB", "Missing USB wear must be identified.");
Check(!group.NeedsLook && group.HealthWord == "No reading", "Unknown health must stay neutral.");
usb.Life = 0;
Check(group.NeedsLook && group.LifeText == "0%" && group.HealthWord == "Early warning", "A reported zero must warn.");
usb.Life = null;
usb.Status = "Bad";
Check(group.IsFailing, "A SMART failure must warn even when wear is unknown.");

var dir = Directory.CreateTempSubdirectory("rigsight-wear-");
try
{
    string path = Path.Combine(dir.FullName, "history.db");
    using (var db = RigsightDb.OpenWriter(path))
    {
        usb.Status = "Unknown";
        usb.Life = usb.ReadGb = usb.WrittenGb = 0;
        db.UpsertDriveHealth(1, usb);
        usb.Status = "Good";
        db.UpsertDriveHealth(2, usb);
        usb.Status = "Unknown";
        usb.PowerOnHours = 50;
        db.UpsertDriveHealth(3, usb);
    }
    // Reproduce a database written before this migration existed.
    using (var sql = new SqliteConnection($"Data Source={path};Pooling=False"))
    {
        sql.Open();
        using var cmd = sql.CreateCommand();
        cmd.CommandText = "DELETE FROM meta WHERE key = 'drive-smart-reset-values-v1'";
        cmd.ExecuteNonQuery();
    }
    using (var db = RigsightDb.OpenWriter(path))
    {
        var rows = db.GetDriveHealthDays(0, "usb");
        Check(rows.Count == 3, "All history days must be preserved.");
        Check(rows[0] is { Life: null, ReadGb: null, WrittenGb: null }, "The empty SMART snapshot must lose its placeholder values.");
        Check(rows[1].Life == 0 && rows[2].Life == 0, "Corroborated zero readings must survive migration.");
        usb.Status = "Good";
        usb.Life = 83;
        db.UpsertDriveHealth(4, usb);
        usb.Status = "Unknown";
        usb.Life = null;
        db.UpsertDriveHealth(4, usb);
        Check(db.GetDriveHealthDays(4, "usb").Single().Life == 83, "A missed read must preserve the day's last measured life.");
    }
}
finally
{
    dir.Delete(recursive: true);
}
Console.WriteLine("Drive wear checks passed.");
