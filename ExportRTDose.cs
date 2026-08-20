using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;

[assembly: ESAPIScript(IsWriteable = false)]

namespace VMS.TPS
{
    public class Script
    {
        // Change these paths for your local setup.
        private const string PythonExe = @"V:\Public\Users\Pratik Samant\PythonLauncher\Samant\venvTBOX\python.exe\Scripts\python.exe";
        private const string PythonScript = @"V:\Public\Users\Pratik Samant\Projects\ExportRTDose\write_rtdose.py";

        public void Execute(ScriptContext context)
        {
            if (context == null)
                throw new ArgumentNullException("context");

            Patient patient = context.Patient;
            PlanSetup plan = context.PlanSetup;

            if (patient == null)
                throw new ApplicationException("No patient is open.");

            if (plan == null)
                throw new ApplicationException("No active plan is open. This script exports context.PlanSetup only.");

            if (!plan.IsDoseValid || plan.Dose == null)
                throw new ApplicationException("The active plan has no valid dose.");

            Dose dose = plan.Dose;

            SaveFileDialog dlg = new SaveFileDialog
            {
                Title = "Export RT Dose DICOM",
                Filter = "DICOM RT Dose (*.dcm)|*.dcm|All files (*.*)|*.*",
                FileName = MakeValidFileName(
                    string.Format(
                        "{0}_{1}_{2}_RTDOSE.dcm",
                        SafeString(patient.Id, "Patient"),
                        SafeString(context.Course != null ? context.Course.Id : "Course", "Course"),
                        SafeString(plan.Id, "Plan")))
            };

            bool? result = dlg.ShowDialog();

            if (result != true)
                return;

            string outputDicomPath = dlg.FileName;

            string workDir = Path.Combine(
                Path.GetTempPath(),
                "EsapiRtDoseExport_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(workDir);

            string rawDosePath = Path.Combine(workDir, "dose_i32_le.raw");
            string metaPath = Path.Combine(workDir, "dose_meta.json");

            // ESAPI stores dose as internal integer voxels. We export those and separately store the Gy scaling.
            int rawMax = WriteRawDoseGrid(dose, rawDosePath);

            // Force ESAPI dose-value conversion into absolute physical dose.
            DoseValuePresentation originalDosePresentation = plan.DoseValuePresentation;

            double rawToGy;

            try
            {
                plan.DoseValuePresentation = DoseValuePresentation.Absolute;

                DoseValue maxDoseValue = dose.VoxelToDoseValue(rawMax);
                double maxDoseGy = ConvertDoseValueToGy(maxDoseValue);

                rawToGy = rawMax > 0 ? maxDoseGy / rawMax : 0.0;
            }
            finally
            {
                plan.DoseValuePresentation = originalDosePresentation;
            }

            VMS.TPS.Common.Model.API.Image image = null;
            string studyUid = "";
            string frameOfReferenceUid = "";

            try
            {
                if (plan.StructureSet != null)
                    image = plan.StructureSet.Image;

                if (image != null)
                {
                    frameOfReferenceUid = SafeString(image.FOR, "");

                    if (image.Series != null && image.Series.Study != null)
                        studyUid = SafeString(image.Series.Study.UID, "");
                }
            }
            catch
            {
                // If image metadata is unavailable, Python will generate replacement UIDs.
                studyUid = "";
                frameOfReferenceUid = "";
            }

            WriteMetadataJson(
                metaPath,
                patient,
                context.Course != null ? context.Course.Id : "",
                plan,
                dose,
                rawDosePath,
                outputDicomPath,
                rawMax,
                rawToGy,
                studyUid,
                frameOfReferenceUid,
                GetEsapiAssemblyVersion());

            RunPython(metaPath);

            MessageBox.Show(
                "RT Dose DICOM written:\n\n" + outputDicomPath,
                "RT Dose export complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private static int WriteRawDoseGrid(Dose dose, string rawDosePath)
        {
            int[,] plane = new int[dose.XSize, dose.YSize];
            int rawMax = 0;

            using (BinaryWriter bw = new BinaryWriter(File.Open(rawDosePath, FileMode.Create, FileAccess.Write)))
            {
                // File order is z, then y, then x. Python reshapes this as [z, y, x].
                for (int z = 0; z < dose.ZSize; z++)
                {
                    dose.GetVoxels(z, plane);

                    for (int y = 0; y < dose.YSize; y++)
                    {
                        for (int x = 0; x < dose.XSize; x++)
                        {
                            int v = plane[x, y];

                            bw.Write(v);

                            if (v > rawMax)
                                rawMax = v;
                        }
                    }
                }
            }

            return rawMax;
        }

        private static void WriteMetadataJson(
            string metaPath,
            Patient patient,
            string courseId,
            PlanSetup plan,
            Dose dose,
            string rawDosePath,
            string outputDicomPath,
            int rawMax,
            double rawToGy,
            string studyUid,
            string frameOfReferenceUid,
            string esapiVersion)
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("{");
            sb.AppendLine("  \"patient_id\": " + Q(patient.Id) + ",");
            sb.AppendLine("  \"patient_name\": " + Q(DicomPatientName(patient)) + ",");
            sb.AppendLine("  \"patient_birth_date\": " + Q(patient.DateOfBirth.HasValue ? patient.DateOfBirth.Value.ToString("yyyyMMdd") : "") + ",");
            sb.AppendLine("  \"patient_sex\": " + Q(patient.Sex) + ",");

            sb.AppendLine("  \"course_id\": " + Q(courseId) + ",");
            sb.AppendLine("  \"plan_id\": " + Q(plan.Id) + ",");
            sb.AppendLine("  \"plan_name\": " + Q(plan.Id) + ",");
            sb.AppendLine("  \"plan_uid\": " + Q(plan.UID) + ",");
            sb.AppendLine("  \"dose_uid\": " + Q(dose.UID) + ",");

            sb.AppendLine("  \"study_uid\": " + Q(studyUid) + ",");
            sb.AppendLine("  \"frame_of_reference_uid\": " + Q(frameOfReferenceUid) + ",");

            sb.AppendLine("  \"x_size\": " + dose.XSize + ",");
            sb.AppendLine("  \"y_size\": " + dose.YSize + ",");
            sb.AppendLine("  \"z_size\": " + dose.ZSize + ",");

            sb.AppendLine("  \"x_res_mm\": " + N(dose.XRes) + ",");
            sb.AppendLine("  \"y_res_mm\": " + N(dose.YRes) + ",");
            sb.AppendLine("  \"z_res_mm\": " + N(dose.ZRes) + ",");

            sb.AppendLine("  \"origin_mm\": [" + V(dose.Origin) + "],");
            sb.AppendLine("  \"x_direction\": [" + V(dose.XDirection) + "],");
            sb.AppendLine("  \"y_direction\": [" + V(dose.YDirection) + "],");
            sb.AppendLine("  \"z_direction\": [" + V(dose.ZDirection) + "],");

            sb.AppendLine("  \"raw_max\": " + rawMax + ",");
            sb.AppendLine("  \"raw_to_gy\": " + N(rawToGy) + ",");

            sb.AppendLine("  \"raw_dose_path\": " + Q(rawDosePath) + ",");
            sb.AppendLine("  \"output_dicom_path\": " + Q(outputDicomPath) + ",");
            sb.AppendLine("  \"eclipse_version\": " + Q(esapiVersion));
            sb.AppendLine("}");

            File.WriteAllText(metaPath, sb.ToString(), Encoding.UTF8);
        }

        private static void RunPython(string metaPath)
        {
            string workingDirectory = Path.GetDirectoryName(PythonScript);

            /*MessageBox.Show(
                "Python executable:\n" + PythonExe +
                "\n\nExists: " + File.Exists(PythonExe) +

                "\n\nPython script:\n" + PythonScript +
                "\n\nExists: " + File.Exists(PythonScript) +

                "\n\nWorking directory:\n" + workingDirectory +
                "\n\nExists: " + Directory.Exists(workingDirectory) +

                "\n\nMetadata:\n" + metaPath +
                "\n\nExists: " + File.Exists(metaPath),
                "Python launch diagnostics");
            */ //comments to check that these directories exist


            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = PythonExe,
                Arguments = QuoteArg(PythonScript) + " " + QuoteArg(metaPath),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(PythonScript)
            };

            using (Process p = Process.Start(psi))
            {
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();

                p.WaitForExit();

                if (p.ExitCode != 0)
                {
                    throw new ApplicationException(
                        "Python RT Dose writer failed.\n\nSTDOUT:\n" +
                        stdout +
                        "\n\nSTDERR:\n" +
                        stderr);
                }
            }
        }

        private static double ConvertDoseValueToGy(DoseValue doseValue)
        {
            if (doseValue.IsUndefined())
                throw new ApplicationException("DoseValue is undefined.");

            if (!doseValue.IsAbsoluteDoseValue)
                throw new ApplicationException("Dose is not absolute Gy/cGy. Unit was: " + doseValue.UnitAsString);

            string unit = SafeString(doseValue.UnitAsString, "").Trim();

            if (string.Equals(unit, "Gy", StringComparison.OrdinalIgnoreCase))
                return doseValue.Dose;

            if (string.Equals(unit, "cGy", StringComparison.OrdinalIgnoreCase))
                return doseValue.Dose / 100.0;

            throw new ApplicationException("Unsupported absolute dose unit: " + unit);
        }

        private static string DicomPatientName(Patient patient)
        {
            string last = SafeString(patient.LastName, "");
            string first = SafeString(patient.FirstName, "");
            string middle = SafeString(patient.MiddleName, "");

            return last + "^" + first + "^" + middle;
        }

        private static string MakeValidFileName(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');

            return s;
        }

        private static string SafeString(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static string Q(string s)
        {
            if (s == null)
                s = "";

            return "\"" +
                   s.Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\r", "\\r")
                    .Replace("\n", "\\n") +
                   "\"";
        }

        private static string N(double value)
        {
            return value.ToString("G17", CultureInfo.InvariantCulture);
        }

        private static string V(VVector v)
        {
            return N(v.x) + ", " + N(v.y) + ", " + N(v.z);
        }

        private static string QuoteArg(string path)
        {
            return "\"" + path.Replace("\"", "\\\"") + "\"";
        }

        private static string GetEsapiAssemblyVersion()
        {
            try
            {
                System.Version version = typeof(ScriptContext).Assembly.GetName().Version;
                return version != null ? version.ToString() : "";
            }
            catch
            {
                return "";
            }
        }
    }
}