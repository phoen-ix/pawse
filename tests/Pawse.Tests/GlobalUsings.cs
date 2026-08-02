// Mirror of src/Pawse/GlobalUsings.cs: UseWindowsForms injects a global
// `using System.Windows.Forms;`, whose Keys type would make every unqualified
// `Keys` reference ambiguous with Pawse.Core.Keys.
global using Keys = Pawse.Core.Keys;
