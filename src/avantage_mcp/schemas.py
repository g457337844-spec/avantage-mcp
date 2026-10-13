"""Reusable validated parameter types for the public MCP tools."""

from pathlib import PureWindowsPath
from typing import Annotated, Literal
from pydantic import AfterValidator, BaseModel, ConfigDict, Field, model_validator


def _absolute_windows_path(value: str) -> str:
    if "\0" in value or not PureWindowsPath(value).is_absolute():
        raise ValueError("Use an absolute Windows path, such as C:\\data\\spectrum.VGD.")
    return value

SessionId = Annotated[str, Field(min_length=1, max_length=128, description="Session id returned by avantage_open_spectrum.")]
FilePath = Annotated[str, Field(min_length=1, max_length=32767, description="Absolute local Windows file path. Unicode paths are supported."), AfterValidator(_absolute_windows_path)]
Offset = Annotated[int, Field(ge=0, description="Zero-based first point in the requested page.")]
PageSize = Annotated[int, Field(ge=1, le=10000, description="Maximum data points to return, between 1 and 10000.")]
ExportFormat = Literal["csv", "json"]
EnergyShift = Annotated[float, Field(allow_inf_nan=False, description="Signed binding-energy change in eV; positive shifts binding energy upward and kinetic energy downward.")]
PeakId = Annotated[str, Field(min_length=1, max_length=128, description="Peak id returned when adding a peak to this session.")]
Finite = Annotated[float, Field(allow_inf_nan=False)]
Nonnegative = Annotated[float, Field(ge=0, allow_inf_nan=False)]
Positive = Annotated[float, Field(gt=0, allow_inf_nan=False)]
Fraction = Annotated[float, Field(ge=0, le=1, allow_inf_nan=False)]
Label = Annotated[str, Field(min_length=1, max_length=256)]
EnergyScale = Literal["binding", "kinetic"]
PeakAttribute = Literal["area", "centre", "height", "fwhm", "gl", "tail_height", "tail_mix", "tail_exponential"]
ConstraintType = Literal["fixed", "range", "plus_current", "minus_current", "plus_minus_current", "linked", "linked_range", "linked_range_percent"]
ConstraintOperator = Literal["none", "plus", "minus", "multiply", "divide"]
BackgroundKind = Literal["linear", "shirley", "smart", "tougaard", "simple"]
GLCombineMethod = Literal["product", "sum", "convolution"]
TougaardType = Literal["bc", "bcd"]
EndpointAverage = Annotated[int, Field(ge=0, le=100, description="Native endpoint averaging setting; the selected range must contain enough points.")]
ShirleyIterations = Annotated[int, Field(ge=1, le=10000, description="Maximum native Shirley iterations.")]
FitIterations = Annotated[int, Field(ge=1, le=100000, description="Maximum native simplex iterations.")]
FitTimeout = Annotated[int, Field(ge=1, le=600, description="Isolated fit timeout in seconds; keep the MCP bridge/client timeout longer than this.")]


class PeakSpec(BaseModel):
    """Explicit initial parameters for a native Avantage peak object."""

    model_config = ConfigDict(extra="forbid")
    label: Label
    centre_ev: Finite
    height: Nonnegative
    fwhm_ev: Positive
    energy_scale: EnergyScale = "binding"
    gaussian_lorentz: Fraction = 0.3
    gl_combine_method: GLCombineMethod = "product"
    area: Nonnegative = 0
    area_ratio: Nonnegative = 1
    height_ratio: Nonnegative = 1
    tail_mix: Fraction = 0
    tail_height: Nonnegative = 0
    tail_exponential: Nonnegative = 0


class PeakPatch(BaseModel):
    """Only the supplied peak fields are changed; null and empty patches fail."""

    model_config = ConfigDict(extra="forbid")
    label: Label | None = None
    centre_ev: Finite | None = None
    height: Nonnegative | None = None
    fwhm_ev: Positive | None = None
    energy_scale: EnergyScale | None = None
    gaussian_lorentz: Fraction | None = None
    gl_combine_method: GLCombineMethod | None = None
    area: Nonnegative | None = None
    area_ratio: Nonnegative | None = None
    height_ratio: Nonnegative | None = None
    tail_mix: Fraction | None = None
    tail_height: Nonnegative | None = None
    tail_exponential: Nonnegative | None = None

    @model_validator(mode="before")
    @classmethod
    def valid_patch(cls, value: object) -> object:
        if isinstance(value, dict):
            if not value or any(item is None for item in value.values()):
                raise ValueError("Supply at least one non-null peak field; omit unchanged fields.")
            if "energy_scale" in value and "centre_ev" not in value:
                raise ValueError("energy_scale requires centre_ev in the same update.")
        return value


class ConstraintSpec(BaseModel):
    """Native peak constraint values, optionally linked to another peak."""

    model_config = ConfigDict(extra="forbid")
    type: ConstraintType
    value1: Finite
    value2: Finite = 0
    value3: Finite = 0
    energy_scale: EnergyScale = "binding"
    reference_peak_id: PeakId | None = None
    operator: ConstraintOperator = "none"

    @model_validator(mode="after")
    def validate_relationship(self) -> "ConstraintSpec":
        linked = self.type in {"linked", "linked_range", "linked_range_percent"}
        if linked:
            if not self.reference_peak_id or self.operator == "none":
                raise ValueError("Linked constraints require reference_peak_id and an arithmetic operator.")
            if self.operator == "divide" and self.value1 == 0:
                raise ValueError("A linked divisor cannot be zero.")
        elif self.reference_peak_id is not None or self.operator != "none":
            raise ValueError("Only linked constraints accept reference_peak_id or an arithmetic operator.")
        if self.type == "range" and self.value1 > self.value2:
            raise ValueError("Range lower bound must not exceed its upper bound.")
        return self
