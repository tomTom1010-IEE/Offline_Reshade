// Offline ReShade independent Add-on service ABI v1.
// SPDX-License-Identifier: BSD-3-Clause
#pragma once
#include "reshade_api.hpp"

// An Add-on may export OfflineReShadeAddonServiceV1 with this signature.
// Invoked on the runtime owner thread, without opening or executing an ImGui UI.
// method is "get", "set", or "action". request is UTF-8 JSON.
// Return UTF-8 JSON owned by the Add-on, valid until its next call (max 1 MiB).
// get is read-only and returns {schemaVersion:1, parameters:[...], actions:[...] }.
// set/action must validate IDs/types, apply synchronously, and return the updated
// get schema, or {error:"..."}. Never report a queued write as applied.
// Parameters use stable id, label, type, uiType, value (array), optional min/max/
// step/items/ui_tooltip/ui_text/ui_units/readOnly. Do not expose raw ImGui IDs.
// Private-state persistence/undo is NOT supplied by the host.
using offline_addon_service_v1 = const char *(*)(reshade::api::effect_runtime *, const char *method, const char *request);
