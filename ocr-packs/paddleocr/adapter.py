#!/usr/bin/env python3
"""Offline PaddleOCR adapter for AI Screenshot.

The executable contract is intentionally tiny:

    paddleocr-adapter --input /absolute/image.png --output json
    paddleocr-adapter --health-check
    paddleocr-adapter --worker

Only the final JSON object is written to stdout. Framework logs are redirected
to stderr so the Swift host can decode stdout without heuristics.
"""

from __future__ import annotations

import argparse
import contextlib
import json
import os
from pathlib import Path
import platform
import sys
import time
from typing import Any


PACK_VERSION = "1.1.0"
# Protocol 2 adds the optional per-line `lines` array (text, confidence, box).
PROTOCOL_VERSION = 2
DETECTION_MODEL = "PP-OCRv5_mobile_det_onnx"
RECOGNITION_MODEL = "PP-OCRv5_mobile_rec_onnx"
DEFAULT_DETECTION_SIDE_LIMIT = 2560


def pack_root() -> Path:
    # PyInstaller extracts bundled data under _MEIPASS. The development
    # launcher keeps the same models/ layout next to this source file.
    frozen_root = getattr(sys, "_MEIPASS", None)
    return Path(frozen_root) if frozen_root else Path(__file__).resolve().parent


def model_directories() -> tuple[Path, Path]:
    override = os.environ.get("AI_SCREENSHOT_PADDLE_MODELS_DIR")
    root = Path(override).expanduser().resolve() if override else pack_root() / "models"
    return root / DETECTION_MODEL, root / RECOGNITION_MODEL


def validate_models() -> tuple[Path, Path]:
    detection, recognition = model_directories()
    required = (
        detection / "inference.onnx",
        detection / "inference.yml",
        recognition / "inference.onnx",
        recognition / "inference.yml",
    )
    missing = [str(path) for path in required if not path.is_file()]
    if missing:
        raise RuntimeError("missing model files: " + ", ".join(missing))
    return detection, recognition


def create_pipeline() -> Any:
    detection, recognition = validate_models()
    # Prevent PaddleX from probing model hosts. Both model directories are
    # explicit, and the finished enhancement pack performs no network access.
    os.environ["PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK"] = "True"
    os.environ["PADDLE_PDX_MODEL_SOURCE"] = "BOS"
    from paddleocr import PaddleOCR

    return PaddleOCR(
        use_doc_orientation_classify=False,
        use_doc_unwarping=False,
        use_textline_orientation=False,
        text_detection_model_name="PP-OCRv5_mobile_det",
        text_detection_model_dir=str(detection),
        text_recognition_model_name="PP-OCRv5_mobile_rec",
        text_recognition_model_dir=str(recognition),
        engine="onnxruntime",
        device="cpu",
    )


def result_payload(result: Any) -> dict[str, Any]:
    value = result.json
    if isinstance(value, str):
        value = json.loads(value)
    if not isinstance(value, dict):
        raise RuntimeError("PaddleOCR returned an unsupported result")
    nested = value.get("res", value)
    return nested if isinstance(nested, dict) else {}


def line_box(payload: dict[str, Any], index: int) -> list[float] | None:
    """Axis-aligned pixel box [minX, minY, maxX, maxY] (top-left origin).

    Prefers the recognition polygon and falls back to `rec_boxes`. Returns
    None when the pipeline did not report a usable position for the line.
    """
    polygons = payload.get("rec_polys")
    try:
        if polygons is not None and index < len(polygons):
            points = [(float(point[0]), float(point[1])) for point in polygons[index]]
            if points:
                xs = [point[0] for point in points]
                ys = [point[1] for point in points]
                box = [min(xs), min(ys), max(xs), max(ys)]
                if box[2] > box[0] and box[3] > box[1]:
                    return [round(value, 2) for value in box]
        boxes = payload.get("rec_boxes")
        if boxes is not None and index < len(boxes):
            values = [float(value) for value in boxes[index]]
            if len(values) == 4 and values[2] > values[0] and values[3] > values[1]:
                return [round(value, 2) for value in values]
    except (TypeError, ValueError, IndexError):
        return None
    return None


def recognize(
    input_path: Path,
    pipeline: Any | None = None,
    detection_side_limit: int = DEFAULT_DETECTION_SIDE_LIMIT,
) -> dict[str, Any]:
    if not input_path.is_file():
        raise RuntimeError(f"input image does not exist: {input_path}")

    started = time.perf_counter()
    owns_pipeline = pipeline is None
    with contextlib.redirect_stdout(sys.stderr):
        if pipeline is None:
            pipeline = create_pipeline()
        loaded = time.perf_counter()
        predictions = list(
            pipeline.predict(
                str(input_path),
                text_det_limit_side_len=max(960, min(int(detection_side_limit), 4096)),
                text_det_limit_type="max",
            )
        )

    texts: list[str] = []
    scores: list[float] = []
    lines: list[dict[str, Any]] = []
    for prediction in predictions:
        payload = result_payload(prediction)
        page_texts = payload.get("rec_texts", [])
        page_scores = payload.get("rec_scores", [])
        for index, raw_text in enumerate(page_texts):
            text = str(raw_text).strip()
            if not text:
                continue
            texts.append(text)
            line_score: float | None = None
            if index < len(page_scores):
                score = float(page_scores[index])
                if 0.0 <= score <= 1.0:
                    scores.append(score)
                    line_score = round(score, 6)
            # Per-line geometry lets the host build the same layout it gets
            # from Apple Vision (overlay translation, table detection).
            lines.append({
                "text": text,
                "confidence": line_score,
                "box": line_box(payload, index),
            })

    confidence = sum(scores) / len(scores) if scores else 0.0
    finished = time.perf_counter()
    return {
        "text": "\n".join(texts),
        "confidence": round(confidence, 6),
        "lines": lines,
        "engine": "paddleOCR",
        "version": PACK_VERSION,
        "protocolVersion": PROTOCOL_VERSION,
        "metrics": {
            "modelLoadMilliseconds": round((loaded - started) * 1000, 1) if owns_pipeline else 0,
            "inferenceMilliseconds": round((finished - loaded) * 1000, 1),
            "totalMilliseconds": round((finished - started) * 1000, 1),
        },
    }


def write_json(value: dict[str, Any]) -> None:
    print(json.dumps(value, ensure_ascii=False, separators=(",", ":")), flush=True)


def worker() -> int:
    started = time.perf_counter()
    with contextlib.redirect_stdout(sys.stderr):
        pipeline = create_pipeline()
    write_json({
        "event": "ready",
        "ok": True,
        "engine": "paddleOCR",
        "packVersion": PACK_VERSION,
        "startupMilliseconds": round((time.perf_counter() - started) * 1000, 1),
    })

    for raw_line in sys.stdin:
        line = raw_line.strip()
        if not line:
            continue
        request_id: str | None = None
        try:
            request = json.loads(line)
            if not isinstance(request, dict):
                raise RuntimeError("worker request must be a JSON object")
            request_id = str(request.get("id", "")) or None
            command = request.get("command", "recognize")
            if command == "ping":
                write_json({"id": request_id, "ok": True, "event": "pong"})
                continue
            if command == "shutdown":
                write_json({"id": request_id, "ok": True, "event": "stopping"})
                return 0
            if command != "recognize":
                raise RuntimeError(f"unsupported worker command: {command}")
            input_value = request.get("input")
            if not isinstance(input_value, str) or not input_value:
                raise RuntimeError("worker recognition requires an input path")
            side_limit = request.get("detectionSideLimit", DEFAULT_DETECTION_SIDE_LIMIT)
            response = recognize(Path(input_value), pipeline, int(side_limit))
            response.update({"id": request_id, "ok": True})
            write_json(response)
        except Exception as error:
            write_json({
                "id": request_id,
                "ok": False,
                "error": str(error),
                "engine": "paddleOCR",
                "version": PACK_VERSION,
            })
    return 0


def health_check() -> dict[str, Any]:
    validate_models()
    with contextlib.redirect_stdout(sys.stderr):
        import onnxruntime
        import paddleocr

    return {
        "ok": True,
        "engine": "paddleOCR",
        "packVersion": PACK_VERSION,
        "paddleOCRVersion": paddleocr.__version__,
        "onnxRuntimeVersion": onnxruntime.__version__,
        "architecture": platform.machine(),
        "offline": True,
    }


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(prog="paddleocr-adapter")
    parser.add_argument("--input", type=Path)
    parser.add_argument("--output", choices=("json",), default="json")
    parser.add_argument("--health-check", action="store_true")
    parser.add_argument("--worker", action="store_true")
    args = parser.parse_args()
    if not args.health_check and not args.worker and args.input is None:
        parser.error("--input is required unless --health-check or --worker is used")
    return args


def main() -> int:
    args = parse_arguments()
    try:
        if args.worker:
            return worker()
        response = health_check() if args.health_check else recognize(args.input)
        write_json(response)
        return 0
    except Exception as error:  # the host surfaces stderr to the user
        print(f"PaddleOCR adapter failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
