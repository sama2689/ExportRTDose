import json
import sys
from datetime import datetime
from pathlib import Path

import numpy as np
from pydicom.dataset import Dataset, FileDataset
from pydicom.sequence import Sequence
from pydicom.tag import Tag
from pydicom.uid import ExplicitVRLittleEndian, generate_uid


RT_DOSE_STORAGE = "1.2.840.10008.5.1.4.1.1.481.2"
RT_PLAN_STORAGE = "1.2.840.10008.5.1.4.1.1.481.5"


def ds_float(value: float) -> str:
    """Return a compact DICOM DS-compatible numeric string."""
    return f"{float(value):.12g}"


def dicom_sex(value: str) -> str:
    v = (value or "").strip().upper()
    if v.startswith("M"):
        return "M"
    if v.startswith("F"):
        return "F"
    if v.startswith("O"):
        return "O"
    return ""


def main(meta_path: str) -> None:
    with open(meta_path, "r", encoding="utf-8-sig") as f:
        meta = json.load(f)

    x_size = int(meta["x_size"])
    y_size = int(meta["y_size"])
    z_size = int(meta["z_size"])

    raw_path = Path(meta["raw_dose_path"])
    out_path = Path(meta["output_dicom_path"])

    expected_voxels = x_size * y_size * z_size

    raw = np.fromfile(raw_path, dtype="<i4")
    if raw.size != expected_voxels:
        raise ValueError(
            f"Raw dose voxel count mismatch: got {raw.size}, expected {expected_voxels}"
        )

    raw = raw.reshape((z_size, y_size, x_size))
    dose_gy = raw.astype(np.float64) * float(meta["raw_to_gy"])

    # Store as unsigned 32-bit RT Dose pixel data.
    #
    # DICOM RT Dose uses integer Pixel Data plus DoseGridScaling:
    # physical dose = pixel value * DoseGridScaling.
    max_dose_gy = float(np.max(dose_gy))
    max_uint = np.iinfo(np.uint32).max

    if max_dose_gy > 0:
        dose_grid_scaling = max_dose_gy / max_uint
        pixels = np.rint(dose_gy / dose_grid_scaling).clip(0, max_uint).astype("<u4")
    else:
        dose_grid_scaling = 1.0
        pixels = np.zeros((z_size, y_size, x_size), dtype="<u4")

    now = datetime.now()
    now_date = now.strftime("%Y%m%d")
    now_time = now.strftime("%H%M%S")

    sop_instance_uid = generate_uid()
    series_uid = generate_uid()

    study_uid = meta.get("study_uid") or generate_uid()
    frame_uid = meta.get("frame_of_reference_uid") or generate_uid()

    file_meta = Dataset()
    file_meta.FileMetaInformationVersion = b"\x00\x01"
    file_meta.MediaStorageSOPClassUID = RT_DOSE_STORAGE
    file_meta.MediaStorageSOPInstanceUID = sop_instance_uid
    file_meta.TransferSyntaxUID = ExplicitVRLittleEndian
    file_meta.ImplementationClassUID = generate_uid()

    ds = FileDataset(
        str(out_path),
        {},
        file_meta=file_meta,
        preamble=b"\0" * 128,
    )

    ds.is_little_endian = True
    ds.is_implicit_VR = False

    # Patient / study / series
    ds.SpecificCharacterSet = "ISO_IR 100"
    ds.PatientName = meta.get("patient_name", "")
    ds.PatientID = meta.get("patient_id", "")
    ds.PatientBirthDate = meta.get("patient_birth_date", "")
    ds.PatientSex = dicom_sex(meta.get("patient_sex", ""))

    ds.StudyInstanceUID = study_uid
    ds.SeriesInstanceUID = series_uid
    ds.SOPClassUID = RT_DOSE_STORAGE
    ds.SOPInstanceUID = sop_instance_uid
    ds.Modality = "RTDOSE"

    ds.StudyDate = now_date
    ds.StudyTime = now_time
    ds.SeriesDate = now_date
    ds.SeriesTime = now_time
    ds.ContentDate = now_date
    ds.ContentTime = now_time
    ds.InstanceCreationDate = now_date
    ds.InstanceCreationTime = now_time

    ds.StudyID = meta.get("course_id", "")[:16] or "ESAPI"
    ds.SeriesNumber = "9001"
    ds.InstanceNumber = "1"

    ds.Manufacturer = "Varian Medical Systems"
    ds.ManufacturerModelName = "Eclipse ESAPI dose export"
    ds.SoftwareVersions = meta.get("eclipse_version", "")

    ds.FrameOfReferenceUID = frame_uid
    ds.PositionReferenceIndicator = ""

    # RT Dose module
    ds.DoseUnits = "GY"
    ds.DoseType = "PHYSICAL"
    ds.DoseSummationType = "PLAN"
    ds.DoseGridScaling = ds_float(dose_grid_scaling)

    # Geometry
    origin = meta["origin_mm"]
    x_dir = meta["x_direction"]
    y_dir = meta["y_direction"]

    ds.ImagePositionPatient = [ds_float(v) for v in origin]
    ds.ImageOrientationPatient = [ds_float(v) for v in (x_dir + y_dir)]

    # DICOM PixelSpacing is [row spacing, column spacing].
    ds.PixelSpacing = [
        ds_float(meta["y_res_mm"]),
        ds_float(meta["x_res_mm"]),
    ]

    ds.SliceThickness = ds_float(meta["z_res_mm"])

    # Relative offsets from first dose plane.
    ds.GridFrameOffsetVector = [
        ds_float(i * float(meta["z_res_mm"])) for i in range(z_size)
    ]
    ds.FrameIncrementPointer = [Tag(0x3004, 0x000C)]

    # Image pixel module
    ds.SamplesPerPixel = 1
    ds.PhotometricInterpretation = "MONOCHROME2"
    ds.NumberOfFrames = str(z_size)
    ds.Rows = y_size
    ds.Columns = x_size

    ds.BitsAllocated = 32
    ds.BitsStored = 32
    ds.HighBit = 31
    ds.PixelRepresentation = 0

    ds.PixelData = pixels.tobytes(order="C")

    # Reference the RT Plan if ESAPI provided its UID.
    plan_uid = meta.get("plan_uid", "")
    if plan_uid:
        ref_plan = Dataset()
        ref_plan.ReferencedSOPClassUID = RT_PLAN_STORAGE
        ref_plan.ReferencedSOPInstanceUID = plan_uid
        ds.ReferencedRTPlanSequence = Sequence([ref_plan])

    ds.save_as(str(out_path), write_like_original=False)

    print(f"Wrote {out_path}")
    print(f"Shape z,y,x = {pixels.shape}")
    print(f"Max dose Gy = {max_dose_gy:.6f}")
    print(f"DoseGridScaling = {dose_grid_scaling:.12g}")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("Usage: python write_rtdose.py dose_meta.json")
    main(sys.argv[1])