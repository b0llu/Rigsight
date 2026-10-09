using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>Reporting a bug from Settings: what is copied never names the person or the PC, and the app only opens a form.</summary>
[Collection("UI")]
public class ProblemReportTests
{
    [Fact]
    public void The_users_folder_is_taken_out_of_every_path_and_nothing_else_is_touched()
    {
        string text = "[agent] Data in C:\\Users\\saman\\AppData\\Local\\Rigsight\n   at X() in c:\\users\\Sam Smith\\src\\a.cs:line 4\n[drives] file:///C:/Users/saman/x.db\n"
            + "Hardware:\n  Storage: Samsung SSD 980 1TB\n  Cpu: CPU Core Max\n[app] \"C:\\Users\\saman\\Games\\game.exe\" started";
        string clean = ProblemReport.Scrub(text);
        Assert.DoesNotContain("saman", clean, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("C:\\Users\\(user)\\AppData\\Local\\Rigsight", clean);
        Assert.Contains("C:/Users/(user)/x.db", clean);
        Assert.Contains("\"C:\\Users\\(user)\\Games\\game.exe\" started", clean);
        // A folder name with a space in it goes whole: no part of the name is left behind.
        Assert.Contains("c:\\users\\(user)\\src\\a.cs:line 4", clean);
        Assert.DoesNotContain("Smith", clean);
        // Words that happen to hold a name are left alone: only paths are touched.
        Assert.Contains("Samsung SSD 980 1TB", clean);
        Assert.Contains("CPU Core Max", clean);
        // Nothing to take out: the same text.
        Assert.Equal("Rigsight 0.19.0 problem report", ProblemReport.Scrub("Rigsight 0.19.0 problem report"));
    }

    [Fact]
    public void The_report_for_this_PC_has_no_users_folder_in_it()
    {
        string report = ProblemReport.ForThisPc("0.19.0", connected: true, admin: true, [("Cpu", "Test CPU")], null);
        Assert.StartsWith("Rigsight 0.19.0 problem report", report);
        string folder = Path.GetFileName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Assert.DoesNotContain("\\Users\\" + folder, report, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Report_a_bug_only_opens_the_form_and_Copy_only_copies()
    {
        var settings = Kit.OfflineSettings();
        Ui.Run(() =>
        {
            var vm = new SettingsViewModel(settings, new AgentClient(Ui.Dispatcher), new ReportService(settings));
            string? copied = null, opened = null;
            vm.SetClipboard = text => copied = text;
            vm.OpenInBrowser = url => opened = url;

            Assert.Equal(("Report a bug", "Copy"), (vm.ReportBugText, vm.CopyLogsText));
            vm.ReportBugCommand.Execute(null);
            Assert.Equal(ProblemReport.BugFormUrl, opened);
            // The address is the form and nothing else: no part of the report rides along in it.
            Assert.StartsWith("https://docs.google.com/forms/", opened);
            Assert.DoesNotContain("?", opened);
            // It copies nothing (that is the other button) and so never says "Copied": the form opening is the answer.
            Assert.Null(copied);
            Assert.Equal(("Report a bug", "Copy"), (vm.ReportBugText, vm.CopyLogsText));

            // Copy logs only copies: nothing opens, and its own button says so.
            (copied, opened) = (null, null);
            vm.CopyProblemReportCommand.Execute(null);
            Assert.StartsWith("Rigsight ", copied);
            Assert.Null(opened);
            Assert.Equal(("Report a bug", "Copied"), (vm.ReportBugText, vm.CopyLogsText));
            // It goes back to its name by itself.
            Ui.Pump((int)SettingsViewModel.ButtonSaysFor.TotalMilliseconds + 400);
            Assert.Equal("Copy", vm.CopyLogsText);

            // The clipboard held by another program: the button says the copy failed.
            vm.SetClipboard = _ => throw new System.Runtime.InteropServices.COMException("busy");
            vm.CopyProblemReportCommand.Execute(null);
            Assert.Equal("Couldn't copy", vm.CopyLogsText);
            // No browser to open it with: said on the button, not thrown.
            vm.OpenInBrowser = _ => throw new System.ComponentModel.Win32Exception();
            vm.ReportBugCommand.Execute(null);
            Assert.Equal("Couldn't open", vm.ReportBugText);
        });
    }
}
