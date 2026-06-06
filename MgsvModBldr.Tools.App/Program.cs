// Thin launcher for the modbldr-tools wrapper DLL. All dispatch logic
// lives in MgsvModBldr.Tools.Cli; this exe just forwards argv.
return MgsvModBldr.Tools.Cli.Cli.Run(args);
