#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""PhoneDeck contracts/ 契约测试台校验脚本（仅 Python 标准库）。

检查项：
  C1  所有 JSON 文件可解析（schemas/、samples/、error-codes.json、session-states.json）
  C2  samples/valid/ 全部样本通过其 manifest 登记的 schema 校验，且无未登记样本；
      覆盖 §16.2 六个域（请求确认/会话/能力/音频/配置/错误）
  C3  samples/invalid/ 全部样本确实被对应 schema 拒绝；每条 manifest 记录含
      violates 与 specRef；无未登记样本；覆盖六个域
  C4  error-codes.json 内部一致：分类/编码唯一、编码与 HTTP 状态一致、
      §8 分类全覆盖；与 response.error.schema.json 的 errorCode 枚举完全一致
  C5  session-states.json 内部一致：五态完整、转移引用合法、R1–R7 七条竞态
      规则齐全；逐条模拟 traces，终态必须等于 expect；emits 的错误码已注册
  C6  占位符卫生：样本中 token/secret/password/key 类字段值必须以 PLACEHOLDER
      开头（不得携带真实凭据，规格 §12 SEC-01/SEC-05）
  C7  策略契约文件内部一致（§16.2 第 1/2/3/5/6/7 行）：pairing-and-identity、
      request-dedup、capability-policy、audio-budgets、config-policy 的必填项、
      数值类型、状态标注、注册错误码引用；能力降级矩阵 ↔ 能力枚举、凭据作用域
      ↔ identity schema、墓碑 reasons ↔ tombstone schema 交叉一致；导出样本
      全树键名扫描不得命中 exportForbiddenFields

退出码：0=全部通过；1=存在失败。用法：python tools/validate.py [--root contracts目录]
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

try:  # Windows 控制台中文输出兜底
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

def _looks_like_contracts(path: Path) -> bool:
    return ((path / "error-codes.json").is_file()
            and (path / "session-states.json").is_file()
            and (path / "schemas").is_dir()
            and (path / "tools").is_dir())


def _resolve_contracts_dir(script_file: Path) -> Path:
    """定位 contracts/ 根目录。

    正常情况取脚本上两级（contracts/tools/validate.py → contracts/）。
    若脚本被复制/链接到仓库内其它位置，则沿祖先向上寻找
    含 error-codes.json + schemas/ 的目录（或其 contracts/ 子目录），
    使“从任意目录用绝对路径调用”或“脚本被挪动”都不依赖当前工作目录。
    找不到时返回默认候选，由 main() 统一报错。
    """
    script_file = script_file.resolve()
    candidate = script_file.parent.parent
    if _looks_like_contracts(candidate):
        return candidate
    for ancestor in script_file.parents:
        if _looks_like_contracts(ancestor):
            return ancestor
        child = ancestor / "contracts"
        if _looks_like_contracts(child):
            return child
    return candidate


CONTRACTS_DIR = _resolve_contracts_dir(Path(__file__))
SCHEMAS_DIR_NAME = "schemas"
DOMAINS = {"request-confirmation", "session", "capability", "audio", "config", "error",
           "pairing", "identity"}
EXPECTED_ERROR_CATEGORIES = {
    # docs/OPEN_SOURCE_RELEASE_SPEC.md §8 错误段：未授权、错误目标、能力不支持、忙碌、
    # 配置冲突、过期会话、输入权限不足、音频设备缺失、探针未知、超时；
    # 另含“请求格式/大小错误”（进入输入或音频状态机前拒绝）。
    "bad_request",
    "unauthorized",
    "wrong_target",
    "capability_unsupported",
    "busy",
    "config_conflict",
    "expired_session",
    "input_permission_denied",
    "audio_device_missing",
    "probe_unknown",
    "timeout",
}


# ---------------------------------------------------------------------------
# draft-07 子集校验器（自实现，覆盖本目录 schemas/ 用到的全部关键字）
# ---------------------------------------------------------------------------

class RefResolutionError(Exception):
    pass


def _json_type(instance):
    if instance is None:
        return "null"
    if isinstance(instance, bool):
        return "boolean"
    if isinstance(instance, (int, float)):
        return "number"
    if isinstance(instance, str):
        return "string"
    if isinstance(instance, list):
        return "array"
    if isinstance(instance, dict):
        return "object"
    raise ValueError(f"未知实例类型: {type(instance)!r}")


def _type_matches(instance, expected):
    actual = _json_type(instance)
    if expected == "integer":
        return isinstance(instance, int) and not isinstance(instance, bool)
    if expected == "number":
        return actual == "number"
    return actual == expected


def _equal(a, b):
    """JSON 语义相等（区分 bool 与数字，避免 True==1 误判）。"""
    if isinstance(a, bool) or isinstance(b, bool):
        return isinstance(a, bool) and isinstance(b, bool) and a == b
    if isinstance(a, (int, float)) and isinstance(b, (int, float)):
        return a == b
    if type(a) is not type(b):
        return False
    if isinstance(a, list):
        return len(a) == len(b) and all(_equal(x, y) for x, y in zip(a, b))
    if isinstance(a, dict):
        return a.keys() == b.keys() and all(_equal(a[k], b[k]) for k in a)
    return a == b


def _unescape_pointer(token):
    return token.replace("~1", "/").replace("~0", "~")


def resolve_pointer(doc, pointer):
    node = doc
    for raw in pointer.split("/"):
        if raw == "":
            continue
        token = _unescape_pointer(raw)
        if isinstance(node, dict):
            if token not in node:
                raise RefResolutionError(f"指针不存在: #{pointer}（缺 {token}）")
            node = node[token]
        elif isinstance(node, list):
            if not token.isdigit() or int(token) >= len(node):
                raise RefResolutionError(f"指针不存在: #{pointer}（索引 {token}）")
            node = node[int(token)]
        else:
            raise RefResolutionError(f"指针无法下钻: #{pointer}")
    return node


class SchemaRegistry:
    def __init__(self, schemas_dir: Path):
        self.dir = schemas_dir
        self._cache = {}

    def load(self, name: str):
        if name not in self._cache:
            path = self.dir / name
            if not path.is_file():
                raise RefResolutionError(f"schema 文件不存在: {name}")
            self._cache[name] = json.loads(path.read_text(encoding="utf-8"))
        return self._cache[name]


IGNORED_KEYWORDS = {
    "$schema", "$id", "$comment", "title", "description", "default",
    "examples", "definitions", "readOnly", "writeOnly", "format",  # format 仅注解
}


def validate_instance(instance, schema, registry, base_file, path="$"):
    """返回错误列表；空列表=通过。base_file 为当前 schema 文档文件名（供相对 $ref）。"""
    if not isinstance(schema, dict):
        return [f"{path}: schema 片段必须是对象"]
    errors = []

    if "$ref" in schema:
        ref = schema["$ref"]
        file_part, _, frag = ref.partition("#")
        try:
            if file_part:
                # 兄弟文件引用（如 "definitions.schema.json#/definitions/x"）
                if "/" in file_part or ".." in file_part:
                    raise RefResolutionError(f"仅支持兄弟文件引用: {ref}")
                doc = registry.load(file_part)
                target_base = file_part
            else:
                doc = registry.load(base_file)
                target_base = base_file
            target = resolve_pointer(doc, frag.lstrip("/")) if frag else doc
        except RefResolutionError as exc:
            return [f"{path}: $ref 解析失败 {ref}: {exc}"]
        return validate_instance(instance, target, registry, target_base, path)

    if "type" in schema:
        expected = schema["type"]
        expected_list = expected if isinstance(expected, list) else [expected]
        if not any(_type_matches(instance, t) for t in expected_list):
            errors.append(f"{path}: 期望类型 {expected}，实际 {_json_type(instance)}")
            return errors  # 类型不符时后续关键字无意义

    if "enum" in schema:
        if not any(_equal(instance, member) for member in schema["enum"]):
            errors.append(f"{path}: 值 {instance!r} 不在枚举 {schema['enum']!r}")
    if "const" in schema:
        if not _equal(instance, schema["const"]):
            errors.append(f"{path}: 值必须恒等于 {schema['const']!r}，实际 {instance!r}")

    if isinstance(instance, str):
        if "minLength" in schema and len(instance) < schema["minLength"]:
            errors.append(f"{path}: 长度 {len(instance)} < minLength {schema['minLength']}")
        if "maxLength" in schema and len(instance) > schema["maxLength"]:
            errors.append(f"{path}: 长度 {len(instance)} > maxLength {schema['maxLength']}")
        if "pattern" in schema:
            try:
                if not re.search(schema["pattern"], instance):
                    errors.append(f"{path}: 不匹配 pattern {schema['pattern']!r}")
            except re.error as exc:
                errors.append(f"{path}: pattern 非法 {schema['pattern']!r}: {exc}")

    if isinstance(instance, (int, float)) and not isinstance(instance, bool):
        if "minimum" in schema and instance < schema["minimum"]:
            errors.append(f"{path}: {instance} < minimum {schema['minimum']}")
        if "maximum" in schema and instance > schema["maximum"]:
            errors.append(f"{path}: {instance} > maximum {schema['maximum']}")
        if schema.get("exclusiveMinimum") is True and instance <= schema["minimum"]:
            errors.append(f"{path}: {instance} 需严格大于 {schema['minimum']}")
        if schema.get("exclusiveMaximum") is True and instance >= schema["maximum"]:
            errors.append(f"{path}: {instance} 需严格小于 {schema['maximum']}")

    if isinstance(instance, list):
        if "minItems" in schema and len(instance) < schema["minItems"]:
            errors.append(f"{path}: 项数 {len(instance)} < minItems {schema['minItems']}")
        if "maxItems" in schema and len(instance) > schema["maxItems"]:
            errors.append(f"{path}: 项数 {len(instance)} > maxItems {schema['maxItems']}")
        if schema.get("uniqueItems"):
            for i in range(len(instance)):
                for j in range(i + 1, len(instance)):
                    if _equal(instance[i], instance[j]):
                        errors.append(f"{path}: 第 {i}/{j} 项重复，违反 uniqueItems")
        if "items" in schema:
            for idx, item in enumerate(instance):
                errors.extend(
                    validate_instance(item, schema["items"], registry, base_file,
                                      f"{path}[{idx}]"))

    if isinstance(instance, dict):
        for key in schema.get("required", []):
            if key not in instance:
                errors.append(f"{path}: 缺少必填字段 {key!r}")
        props = schema.get("properties", {})
        for key, value in instance.items():
            if key in props:
                errors.extend(
                    validate_instance(value, props[key], registry, base_file,
                                      f"{path}.{key}"))
            elif "additionalProperties" in schema:
                ap = schema["additionalProperties"]
                if ap is False:
                    errors.append(f"{path}.{key}: 未知字段（additionalProperties=false）")
                elif isinstance(ap, dict):
                    errors.extend(
                        validate_instance(value, ap, registry, base_file,
                                          f"{path}.{key}"))
        if "minProperties" in schema and len(instance) < schema["minProperties"]:
            errors.append(f"{path}: 属性数 < minProperties {schema['minProperties']}")
        if "maxProperties" in schema and len(instance) > schema["maxProperties"]:
            errors.append(f"{path}: 属性数 > maxProperties {schema['maxProperties']}")

    if "not" in schema:
        sub = validate_instance(instance, schema["not"], registry, base_file, path)
        if not sub:
            errors.append(f"{path}: 命中被 not 禁止的形态")

    if "allOf" in schema:
        for idx, sub in enumerate(schema["allOf"]):
            errors.extend(
                validate_instance(instance, sub, registry, base_file,
                                  f"{path}<allOf:{idx}>"))

    if "anyOf" in schema:
        branch_ok = False
        for idx, sub in enumerate(schema["anyOf"]):
            if not validate_instance(instance, sub, registry, base_file, path):
                branch_ok = True
                break
        if not branch_ok:
            errors.append(f"{path}: 不满足 anyOf 任一分支")

    if "oneOf" in schema:
        matches = sum(
            1 for sub in schema["oneOf"]
            if not validate_instance(instance, sub, registry, base_file, path))
        if matches != 1:
            errors.append(f"{path}: oneOf 命中 {matches} 个分支（要求恰好 1）")

    if "if" in schema:
        cond_errors = validate_instance(instance, schema["if"], registry, base_file, path)
        branch = "then" if not cond_errors else "else"
        if branch in schema:
            errors.extend(
                validate_instance(instance, schema[branch], registry, base_file,
                                  f"{path}<{branch}>"))

    return errors


# ---------------------------------------------------------------------------
# 检查器
# ---------------------------------------------------------------------------

class Report:
    def __init__(self):
        self.failures = []

    def check(self, name, failures):
        status = "PASS" if not failures else "FAIL"
        print(f"[{status}] {name}")
        for failure in failures:
            print(f"       - {failure}")
        self.failures.extend(f"{name}: {f}" for f in failures)
        return not failures


def load_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8"))


def check_c1_parse_all(root: Path, report: Report):
    failures = []
    files = sorted(
        p for p in root.rglob("*.json")
        if "node_modules" not in p.parts and "__pycache__" not in p.parts
    )
    if not files:
        return report.check("C1 所有 JSON 可解析", ["contracts/ 下没有任何 JSON 文件"])
    for p in files:
        try:
            load_json(p)
        except Exception as exc:
            failures.append(f"{p.relative_to(root)}: {exc}")
    print(f"       （共解析 {len(files)} 个 JSON 文件）")
    return report.check("C1 所有 JSON 可解析", failures)


def _manifest_entries(root: Path, kind: str, report: Report):
    manifest_path = root / "samples" / kind / "manifest.json"
    failures = []
    if not manifest_path.is_file():
        return None, [f"缺少 {manifest_path}"], set()
    manifest = load_json(manifest_path)
    entries = manifest.get("samples")
    if not isinstance(entries, list) or not entries:
        return None, [f"{manifest_path} 缺少非空 samples 数组"], set()
    actual = {p.name for p in (root / "samples" / kind).glob("*.json")} - {"manifest.json"}
    listed = {e.get("file") for e in entries}
    if actual != listed:
        for extra in sorted(actual - listed):
            failures.append(f"未登记的样本文件: samples/{kind}/{extra}")
        for missing in sorted(listed - actual):
            failures.append(f"manifest 登记但文件不存在: samples/{kind}/{missing}")
    domains = set()
    for e in entries:
        d = e.get("domain")
        if d not in DOMAINS:
            failures.append(f"samples/{kind}/{e.get('file')}: 非法 domain {d!r}")
        else:
            domains.add(d)
    return entries, failures, domains


def check_c2_valid(root: Path, registry: SchemaRegistry, report: Report):
    entries, failures, domains = _manifest_entries(root, "valid", report)
    if entries is None:
        return report.check("C2 合法样本通过 schema 校验", failures)
    for e in entries:
        sample_path = root / "samples" / "valid" / e["file"]
        schema_name = e.get("schema")
        if not (root / SCHEMAS_DIR_NAME / str(schema_name)).is_file():
            failures.append(f"{e['file']}: schema 不存在 {schema_name}")
            continue
        try:
            instance = load_json(sample_path)
        except Exception as exc:
            failures.append(f"{e['file']}: 解析失败 {exc}")
            continue
        errors = validate_instance(
            instance, registry.load(str(schema_name)), registry, str(schema_name))
        if errors:
            failures.append(f"{e['file']}: 应合法却被拒绝 → " + "；".join(errors[:4]))
    missing = DOMAINS - domains
    if missing:
        failures.append(f"合法样本未覆盖域: {sorted(missing)}")
    print(f"       （{len(entries)} 个合法样本，覆盖域 {sorted(domains)}）")
    return report.check("C2 合法样本通过 schema 校验", failures)


def check_c3_invalid(root: Path, registry: SchemaRegistry, report: Report):
    entries, failures, domains = _manifest_entries(root, "invalid", report)
    if entries is None:
        return report.check("C3 非法样本确实被拒绝", failures)
    boundaries = set()
    for e in entries:
        for field in ("violates", "specRef"):
            if not isinstance(e.get(field), str) or not e.get(field, "").strip():
                failures.append(f"{e.get('file')}: manifest 缺少 {field}")
        boundaries.add(e.get("boundary"))
        sample_path = root / "samples" / "invalid" / e["file"]
        schema_name = e.get("schema")
        if not (root / SCHEMAS_DIR_NAME / str(schema_name)).is_file():
            failures.append(f"{e['file']}: schema 不存在 {schema_name}")
            continue
        try:
            instance = load_json(sample_path)
        except Exception as exc:
            failures.append(f"{e['file']}: 解析失败 {exc}")
            continue
        errors = validate_instance(
            instance, registry.load(str(schema_name)), registry, str(schema_name))
        if not errors:
            failures.append(f"{e['file']}: 应被 {schema_name} 拒绝却通过校验")
    missing = DOMAINS - domains
    if missing:
        failures.append(f"非法样本未覆盖域: {sorted(missing)}")
    required_boundaries = {"缺字段", "未来版本", "超长", "未知动作", "错误目标", "过期会话"}
    missing_b = required_boundaries - boundaries
    if missing_b:
        failures.append(f"非法样本未覆盖的关键边界: {sorted(missing_b)}")
    print(f"       （{len(entries)} 个非法样本，覆盖域 {sorted(domains)}，"
          f"边界族 {sorted(b for b in boundaries if b)}）")
    return report.check("C3 非法样本确实被拒绝", failures)


def check_c4_error_codes(root: Path, registry: SchemaRegistry, report: Report):
    failures = []
    path = root / "error-codes.json"
    doc = load_json(path)
    categories = doc.get("categories")
    if not isinstance(categories, list) or not categories:
        return report.check("C4 error-codes.json 内部一致", ["categories 缺失或为空"])

    seen_codes, seen_categories, codes = set(), set(), set()
    for cat in categories:
        cid = cat.get("category")
        code = cat.get("code")
        http_status = cat.get("httpStatus")
        if cid in seen_categories:
            failures.append(f"分类重复: {cid}")
        seen_categories.add(cid)
        if not isinstance(code, str) or not re.fullmatch(r"PD-ERR-\d{3}-\d{2}", code or ""):
            failures.append(f"{cid}: 非法编码 {code!r}")
            continue
        if code in seen_codes:
            failures.append(f"编码重复: {code}")
        seen_codes.add(code)
        codes.add(code)
        if not isinstance(http_status, int) or not 400 <= http_status <= 599:
            failures.append(f"{cid}: 非法 HTTP 状态 {http_status}")
        elif int(code.split("-")[2]) != http_status:
            failures.append(f"{cid}: 编码 {code} 与 httpStatus {http_status} 不一致")
        if not isinstance(cat.get("retryable"), bool):
            failures.append(f"{cid}: retryable 必须为布尔")
        msgs = cat.get("messages")
        if not isinstance(msgs, dict) or not msgs.get("zh") or not msgs.get("en"):
            failures.append(f"{cid}: messages 必须含非空 zh/en")
        if cat.get("status") not in ("current", "target"):
            failures.append(f"{cid}: status 必须为 current/target")
        if not str(cat.get("grounding", "")).strip():
            failures.append(f"{cid}: 缺少 grounding")
        if not cat.get("specRefs"):
            failures.append(f"{cid}: 缺少 specRefs")

    missing = EXPECTED_ERROR_CATEGORIES - seen_categories
    if missing:
        failures.append(f"缺少 §8 错误分类: {sorted(missing)}")
    extra = seen_categories - EXPECTED_ERROR_CATEGORIES
    if extra:
        failures.append(f"出现未在 §8 定义的分类: {sorted(extra)}")

    # 与 response.error.schema.json 的枚举交叉一致
    err_schema = load_json(root / SCHEMAS_DIR_NAME / "response.error.schema.json")
    try:
        enum_node = resolve_pointer(err_schema, "definitions/errorCode/enum")
    except RefResolutionError as exc:
        enum_node = None
        failures.append(f"response.error.schema.json 缺少 errorCode 枚举: {exc}")
    if enum_node is not None:
        enum_set = set(enum_node)
        if enum_set != codes:
            failures.append(
                f"errorCode 枚举与 error-codes.json 不一致："
                f"仅枚举有 {sorted(enum_set - codes)}；仅注册表有 {sorted(codes - enum_set)}")

    # session-states.json emits 引用的错误码必须已注册
    ss_path = root / "session-states.json"
    if ss_path.is_file():
        ss = load_json(ss_path)
        for tr in ss.get("transitions", []):
            for emitted in tr.get("emits", []):
                if emitted not in codes:
                    failures.append(f"session-states 转移 {tr.get('id')} emits 未注册错误码 {emitted}")
    print(f"       （{len(categories)} 个错误分类，编码 {len(codes)} 个）")
    return report.check("C4 error-codes.json 内部一致", failures)


def _simulate_trace(trace, transitions, states):
    """按转移表模拟 trace，返回 (failures, final_state, final_active_session)。"""
    failures = []
    state = trace.get("start")
    if state not in states:
        return [f"trace {trace.get('id')}: 非法起始状态 {state}"], None, None
    active = None
    for idx, ev in enumerate(trace.get("events", [])):
        name = ev.get("event")
        ev_session = ev.get("session")
        candidates = [
            t for t in transitions
            if t.get("event") == name and t.get("from") in (state, "*")
        ]
        if not candidates:
            failures.append(
                f"trace {trace.get('id')} 第 {idx + 1} 步 {name!r}: "
                f"状态 {state} 下无此事件的转移")
            return failures, state, active
        matched = []
        for t in candidates:
            sel = t.get("session", "none")
            if sel == "new":
                if isinstance(ev_session, str) and ev_session and ev_session != active:
                    matched.append(t)
            elif sel == "match":
                if isinstance(ev_session, str) and ev_session == active:
                    matched.append(t)
            elif sel == "stale":
                if isinstance(ev_session, str) and ev_session != active:
                    matched.append(t)
            elif sel == "any":
                matched.append(t)
            elif sel == "none":
                if ev_session is None:
                    matched.append(t)
            else:
                failures.append(f"转移 {t.get('id')}: 未知 session 选择器 {sel!r}")
        if not matched:
            failures.append(
                f"trace {trace.get('id')} 第 {idx + 1} 步 {name!r}"
                f"(session={ev_session!r}): 在状态 {state}（活动会话 {active!r}）"
                f"下不匹配任何转移（期望该事件被拒绝/忽略的转移已定义）")
            return failures, state, active
        if len(matched) > 1:
            failures.append(
                f"trace {trace.get('id')} 第 {idx + 1} 步 {name!r}: 匹配多个转移 "
                f"{[t.get('id') for t in matched]}")
            return failures, state, active
        t = matched[0]
        state = t.get("to")
        if state == "IDLE":
            active = None
        elif t.get("session") == "new" and isinstance(ev_session, str):
            active = ev_session
    return failures, state, active


def check_c5_session_states(root: Path, report: Report):
    failures = []
    path = root / "session-states.json"
    doc = load_json(path)
    states = doc.get("states")
    expected_states = {"IDLE", "STARTING", "ACTIVE", "STOPPING", "ERROR"}
    if not isinstance(states, dict) or set(states) != expected_states:
        return report.check(
            "C5 session-states.json 内部一致",
            [f"states 必须恰为 {sorted(expected_states)}，实际 {sorted(states or {})}"])
    if doc.get("initial") != "IDLE":
        failures.append("initial 必须为 IDLE（规格 §9.1）")

    transitions = doc.get("transitions", [])
    transition_ids = set()
    events = set()
    for t in transitions:
        tid = t.get("id")
        if tid in transition_ids:
            failures.append(f"转移 id 重复: {tid}")
        transition_ids.add(tid)
        if t.get("from") != "*" and t.get("from") not in states:
            failures.append(f"转移 {tid}: 非法 from {t.get('from')}")
        if t.get("to") not in states:
            failures.append(f"转移 {tid}: 非法 to {t.get('to')}")
        if t.get("session") not in ("new", "match", "stale", "any", "none"):
            failures.append(f"转移 {tid}: 非法 session 选择器 {t.get('session')!r}")
        events.add(t.get("event"))
        for ref in t.get("ruleRefs", []):
            if not re.fullmatch(r"(R[1-7]|RC[1-9])", ref or ""):
                failures.append(f"转移 {tid}: 非法 ruleRef {ref!r}")

    rules = doc.get("raceRules", [])
    if len(rules) != 7:
        failures.append(f"raceRules 必须为 7 条（§16.3），实际 {len(rules)}")
    rule_ids, rule_trace_map = set(), {}
    for i, rule in enumerate(rules, start=1):
        if rule.get("rule") != i:
            failures.append(f"raceRules[{i - 1}].rule 必须按 §16.3 顺序等于 {i}")
        rid = rule.get("id")
        if rid in rule_ids:
            failures.append(f"规则 id 重复: {rid}")
        rule_ids.add(rid)
        trs = rule.get("traces", [])
        if not trs:
            failures.append(f"规则 {rid}（§16.3 第 {i} 条）缺少可执行 trace")
        rule_trace_map[rid] = set(trs)
        if not str(rule.get("title", "")).strip() or not str(rule.get("specRef", "")).strip():
            failures.append(f"规则 {rid}: 缺少 title/specRef")

    # §16.2 会话行“重连窗口”：重连规则及其轨迹（RC*）与 raceRules 同等校验
    reconnect_rules = doc.get("reconnectRules", [])
    for i, rule in enumerate(reconnect_rules, start=1):
        if rule.get("rule") != i:
            failures.append(f"reconnectRules[{i - 1}].rule 必须顺序等于 {i}")
        rid = rule.get("id")
        if rid in rule_ids:
            failures.append(f"规则 id 重复: {rid}")
        rule_ids.add(rid)
        trs = rule.get("traces", [])
        if not trs:
            failures.append(f"重连规则 {rid} 缺少可执行 trace")
        rule_trace_map[rid] = set(trs)
        if not str(rule.get("title", "")).strip() or not str(rule.get("specRef", "")).strip():
            failures.append(f"重连规则 {rid}: 缺少 title/specRef")

    # §16.2 会话行“终止墓碑”：结构与 reasons 枚举交叉一致
    tomb = doc.get("tombstoneContract")
    if not isinstance(tomb, dict):
        failures.append("缺少 tombstoneContract（§16.2 会话行“终止墓碑”）")
    else:
        schema_path = root / SCHEMAS_DIR_NAME / "session-tombstone.schema.json"
        if not schema_path.is_file():
            failures.append("tombstoneContract.schema 指向的 schema 文件不存在")
        else:
            try:
                t_schema = load_json(schema_path)
                tomb_reasons = set(
                    resolve_pointer(t_schema, "properties/reason/enum"))
                declared = set(tomb.get("reasons", []))
                if tomb_reasons != declared:
                    failures.append(
                        f"墓碑 reasons 与 session-tombstone.schema.json 枚举不一致："
                        f"仅声明有 {sorted(declared - tomb_reasons)}；仅枚举有 "
                        f"{sorted(tomb_reasons - declared)}")
            except RefResolutionError as exc:
                failures.append(f"session-tombstone.schema.json 缺少 reason 枚举: {exc}")
        retention = tomb.get("retentionMs")
        if not isinstance(retention, dict) or not isinstance(retention.get("value"), int) \
                or retention.get("value", 0) <= 0:
            failures.append("tombstoneContract.retentionMs.value 必须为正整数（有界保留）")
        elif not str(retention.get("decision", "")).strip():
            failures.append("tombstoneContract.retentionMs 缺少 decision（D03）")
        if not tomb.get("rules"):
            failures.append("tombstoneContract 缺少 rules")

    traces = doc.get("traces", [])
    trace_ids, trace_rules = set(), {}
    for trace in traces:
        tid = trace.get("id")
        if tid in trace_ids:
            failures.append(f"trace id 重复: {tid}")
        trace_ids.add(tid)
        sim_failures, final_state, final_active = _simulate_trace(trace, transitions, states)
        failures.extend(sim_failures)
        if final_state is not None:
            expect = trace.get("expect") or {}
            if expect.get("state") != final_state:
                failures.append(
                    f"trace {tid}: 模拟终态 {final_state} != 期望 {expect.get('state')}")
            if expect.get("activeSession") != final_active:
                failures.append(
                    f"trace {tid}: 模拟活动会话 {final_active!r} != "
                    f"期望 {expect.get('activeSession')!r}")
        for ev in trace.get("events", []):
            if ev.get("event") not in events:
                failures.append(f"trace {tid}: 未定义事件 {ev.get('event')!r}")
        refs = trace.get("ruleRefs", [])
        trace_rules[tid] = set(refs)
        if not refs:
            failures.append(f"trace {tid}: 缺少 ruleRefs")
        else:
            for ref in refs:
                if ref not in rule_ids:
                    failures.append(f"trace {tid}: 引用未定义规则 {ref}")

    for rid, wanted in rule_trace_map.items():
        for tid in wanted:
            if tid not in trace_ids:
                failures.append(f"规则 {rid} 引用的 trace 不存在: {tid}")
            elif rid not in trace_rules.get(tid, set()):
                failures.append(f"trace {tid} 未回引规则 {rid}")

    print(f"       （{len(states)} 态、{len(transitions)} 条转移、"
          f"{len(rules)} 条竞态规则 + {len(doc.get('reconnectRules', []))} 条重连规则、"
          f"{len(traces)} 条轨迹全部模拟执行）")
    return report.check("C5 session-states.json 内部一致", failures)


# ---------------------------------------------------------------------------
# C7 策略契约文件（§16.2 第 1/2/3/5/6/7 行的参数与生命周期契约）
# ---------------------------------------------------------------------------

PD_ERR_RE = re.compile(r"PD-ERR-\d{3}-\d{2}")


def _check_policy_common(doc, name, failures):
    """公共一致性：status 取值合法；所有 PD-ERR 引用必须已注册。"""
    allowed = {"current", "candidate", "target", "expected-fixtures"}
    text = json.dumps(doc, ensure_ascii=False)
    for code in PD_ERR_RE.findall(text):
        if code not in _check_policy_common.registered_codes:
            failures.append(f"{name}: 引用未注册错误码 {code}")


def check_c7_policy_files(root: Path, report: Report):
    failures = []
    # 注册码集合（error-codes.json）
    codes = set()
    ec_path = root / "error-codes.json"
    if ec_path.is_file():
        codes = {c.get("code") for c in load_json(ec_path).get("categories", [])}
    _check_policy_common.registered_codes = codes

    def load_policy(filename):
        p = root / filename
        if not p.is_file():
            failures.append(f"缺少策略契约文件 {filename}")
            return None
        return load_json(p)

    def check_status(node, where, failures, allowed=("current", "candidate", "target")):
        if isinstance(node, dict) and "status" in node:
            if node["status"] not in allowed:
                failures.append(f"{where}: 非法 status {node['status']!r}")

    # ---- pairing-and-identity.json（§16.2 第 1、2 行）----
    pi = load_policy("pairing-and-identity.json")
    if pi is not None:
        for key in ("discovery", "httpsChannel", "usbPairing", "qrPairing", "pairingErrors", "identity"):
            if key not in pi:
                failures.append(f"pairing-and-identity.json 缺少 {key}（§16.2 第 1/2 行）")
        d = pi.get("discovery", {})
        for key in ("discoveryPort", "magic", "serviceName"):
            if key not in d:
                failures.append(f"pairing-and-identity.json discovery 缺少 {key}")
        qr = pi.get("qrPairing", {})
        for key in ("validSeconds", "maxFailuresPerWindow", "oneTimeMaterial", "manualFallback"):
            if key not in qr:
                failures.append(f"pairing-and-identity.json qrPairing 缺少 {key}（§16.2 第 1 行）")
        if qr.get("validSeconds", {}).get("value") != 120:
            failures.append("qrPairing.validSeconds.value 必须为规格候选值 120（§7）")
        if qr.get("maxFailuresPerWindow", {}).get("value") != 5:
            failures.append("qrPairing.maxFailuresPerWindow.value 必须为规格候选值 5（§7）")
        ident = pi.get("identity", {})
        for key in ("credentialScopes", "storage", "rotation", "revocation", "migration"):
            if key not in ident:
                failures.append(f"pairing-and-identity.json identity 缺少 {key}（§16.2 第 2 行）")
        # 作用域与 identity.schema.json 交叉一致
        id_schema_path = root / SCHEMAS_DIR_NAME / "identity.schema.json"
        if id_schema_path.is_file():
            try:
                scopes_enum = set(resolve_pointer(
                    load_json(id_schema_path), "definitions/credentialScope/enum"))
                declared = set(ident.get("credentialScopes", []))
                if scopes_enum != declared:
                    failures.append(
                        f"credentialScopes 与 identity.schema.json 枚举不一致："
                        f"仅声明有 {sorted(declared - scopes_enum)}；仅枚举有 "
                        f"{sorted(scopes_enum - declared)}")
            except RefResolutionError as exc:
                failures.append(f"identity.schema.json 缺少 credentialScope 枚举: {exc}")
        check_status(pi, "pairing-and-identity.json", failures)

    # ---- request-dedup.json（§16.2 第 3 行）----
    rd = load_policy("request-dedup.json")
    if rd is not None:
        scope = rd.get("requestIdScope", {})
        if scope.get("partitionKey") != ["clientId", "computerId"]:
            failures.append("requestIdScope.partitionKey 必须按 clientId 分区（§16.2 候选参数）")
        if scope.get("sameIdDifferentPayload") != "rejected":
            failures.append("requestIdScope.sameIdDifferentPayload 必须为 rejected（PRO-02）")
        if "canonicalPayloadHash" not in rd:
            failures.append("request-dedup.json 缺少 canonicalPayloadHash（规范化载荷哈希）")
        cache = rd.get("dedupCache", {})
        ttl = cache.get("ttlMs", {})
        if not isinstance(ttl.get("value"), int) or ttl.get("value", 0) <= 0:
            failures.append("dedupCache.ttlMs.value 必须为正整数（30s 已由源码证实）")
        if not str(ttl.get("grounding", "")).strip():
            failures.append("dedupCache.ttlMs 缺少 grounding")
        cap = cache.get("capacity", {})
        if cap.get("strategy") != "reject-or-throttle":
            failures.append("dedupCache.capacity.strategy 必须为 reject-or-throttle")
        if "evict-recent" not in cap.get("forbiddenStrategies", []):
            failures.append("dedupCache.capacity 必须禁止 evict-recent（不得驱逐近期请求制造重复执行）")
        rq = rd.get("retryAndQuery", {})
        if not rq.get("lostResponseQuery", {}).get("strategy"):
            failures.append("retryAndQuery 缺少 lostResponseQuery.strategy（响应丢失后的查询/重试策略）")
        check_status(rd, "request-dedup.json", failures)

    # ---- capability-policy.json（§16.2 第 5 行）----
    cp = load_policy("capability-policy.json")
    if cp is not None:
        for key in ("versionFacts", "maintainedVersionWindow", "protocolWindow",
                    "permissionDenied", "downgradeMatrix"):
            if key not in cp:
                failures.append(f"capability-policy.json 缺少 {key}（§16.2 第 5 行）")
        vf = cp.get("versionFacts", {})
        for platform in ("windows", "android", "macos"):
            if platform not in vf:
                failures.append(f"capability-policy.json versionFacts 缺少 {platform}")
        # 降级矩阵必须覆盖能力枚举全部条目
        defs_path = root / SCHEMAS_DIR_NAME / "definitions.schema.json"
        if defs_path.is_file():
            try:
                caps_enum = set(resolve_pointer(
                    load_json(defs_path), "definitions/capabilityName/enum"))
                matrix = {k for k in cp.get("downgradeMatrix", {}).keys()
                          if not k.startswith("$")}
                if caps_enum != matrix:
                    failures.append(
                        f"downgradeMatrix 与能力枚举不一致：仅枚举有 "
                        f"{sorted(caps_enum - matrix)}；仅矩阵有 {sorted(matrix - caps_enum)}")
            except RefResolutionError as exc:
                failures.append(f"definitions.schema.json 缺少 capabilityName 枚举: {exc}")
        pd = cp.get("permissionDenied", {})
        if set(pd.get("separateFrom", [])) != {"available", "busy", "capturing"}:
            failures.append("permissionDenied 必须与 available/busy/capturing 明确分离")
        check_status(cp, "capability-policy.json", failures)

    # ---- audio-budgets.json（§16.2 第 6 行）----
    ab = load_policy("audio-budgets.json")
    if ab is not None:
        conv = ab.get("sampleRateConversion", {})
        if conv.get("uploadHz") != 48000 or conv.get("uploadChannels") != 1:
            failures.append("sampleRateConversion 必须固化 48kHz/mono 上传基线（MIC-04）")
        if not conv.get("deviceRatesAccepted"):
            failures.append("sampleRateConversion 缺少 deviceRatesAccepted（实际采样率转换）")
        budgets = ab.get("budgets", [])
        required_budgets = {
            "stopLocalCaptureP95", "stopLocalCaptureMax", "sharedQueuePerTarget",
            "preRollPhone", "drainMax", "outputTailSilenceGuard", "stopWaitTotal",
            "tailCollectionBudget", "failureHardLimit", "reconnectRecoveryWindow",
        }
        seen_ids = set()
        for b in budgets:
            bid = b.get("id")
            if bid in seen_ids:
                failures.append(f"audio-budgets 预算 id 重复: {bid}")
            seen_ids.add(bid)
            if not isinstance(b.get("valueMs"), int) or b.get("valueMs", 0) <= 0:
                failures.append(f"audio-budgets {bid}: valueMs 必须为正整数")
            if not str(b.get("grounding", "")).strip() and not b.get("specRef"):
                failures.append(f"audio-budgets {bid}: 缺少 grounding/specRef")
            check_status(b, f"audio-budgets {bid}", failures)
        missing = required_budgets - seen_ids
        if missing:
            failures.append(f"audio-budgets 缺少预算项: {sorted(missing)}")
        if "streamConflict" not in ab or "queueRules" not in ab:
            failures.append("audio-budgets.json 缺少 streamConflict/queueRules（§16.2 音频行）")

    # ---- config-policy.json（§16.2 第 7 行）+ 导出秘密隔离可执行检查 ----
    cfgp = load_policy("config-policy.json")
    if cfgp is not None:
        aw = cfgp.get("atomicWrite", {})
        if not aw.get("steps"):
            failures.append("config-policy atomicWrite 缺少 steps（原子保存算法）")
        if not aw.get("guarantees"):
            failures.append("config-policy atomicWrite 缺少 guarantees")
        if not cfgp.get("crashRecovery", {}).get("vectors"):
            failures.append("config-policy 缺少 crashRecovery.vectors（崩溃恢复期望值，V59）")
        forbidden = cfgp.get("exportIsolation", {}).get("exportForbiddenFields")
        if not forbidden:
            failures.append("config-policy exportIsolation 缺少 exportForbiddenFields")
        else:
            export_path = root / "samples" / "valid" / "config-export-no-secrets.json"
            if export_path.is_file():
                export_doc = load_json(export_path)
                hits = []

                def walk_keys(node, prefix):
                    if isinstance(node, dict):
                        for k, v in node.items():
                            if str(k).lower() in {f.lower() for f in forbidden}:
                                hits.append(f"{prefix}.{k}")
                            walk_keys(v, f"{prefix}.{k}")
                    elif isinstance(node, list):
                        for i, item in enumerate(node):
                            walk_keys(item, f"{prefix}[{i}]")

                walk_keys(export_doc, "$")
                if hits:
                    failures.append(
                        f"合法导出样本命中禁止字段（秘密隔离违例）: {hits}")
                if export_doc.get("containsSecrets") is not False:
                    failures.append("导出样本 containsSecrets 必须为 false")
        check_status(cfgp, "config-policy.json", failures,
                     allowed=("current", "candidate", "target", "expected-fixtures"))

    print("       （pairing/identity、request-dedup、capability、audio-budgets、config 五份策略契约）")
    return report.check("C7 策略契约文件内部一致", failures)


SECRET_KEY_RE = re.compile(r"(token|secret|password|privatekey|apikey|accesskey)", re.I)


def check_c6_placeholder_hygiene(root: Path, report: Report):
    failures = []
    sample_root = root / "samples"
    files = sorted(sample_root.rglob("*.json")) if sample_root.is_dir() else []
    for p in files:
        doc = load_json(p)
        stack = [(doc, "")]

        def walk(node, prefix):
            if isinstance(node, dict):
                for k, v in node.items():
                    where = f"{prefix}.{k}" if prefix else k
                    if isinstance(v, str) and SECRET_KEY_RE.search(str(k)):
                        if not v.startswith("PLACEHOLDER"):
                            failures.append(
                                f"{p.relative_to(root)}: 字段 {where} 的值必须以 "
                                f"PLACEHOLDER 开头（不得携带真实凭据）")
                    walk(v, where)
            elif isinstance(node, list):
                for i, item in enumerate(node):
                    walk(item, f"{prefix}[{i}]")

        while stack:
            node, prefix = stack.pop()
            walk(node, prefix)
    print(f"       （扫描 {len(files)} 个样本文件的敏感字段占位）")
    return report.check("C6 样本占位符卫生", failures)


def main():
    parser = argparse.ArgumentParser(description="PhoneDeck contracts 契约测试台校验")
    parser.add_argument("--root", default=str(CONTRACTS_DIR),
                        help="contracts 目录（默认：脚本上级目录）")
    args = parser.parse_args()
    root = Path(args.root).resolve()
    if not root.is_dir():
        print(f"目录不存在: {root}")
        print("提示：相对路径按当前工作目录解析。请从仓库根目录调用"
              "（python contracts/tools/validate.py），或使用绝对路径/--root 指定 contracts 目录。")
        return 1

    registry = SchemaRegistry(root / SCHEMAS_DIR_NAME)
    report = Report()
    print(f"PhoneDeck contracts 校验：{root}\n")

    check_c1_parse_all(root, report)
    check_c2_valid(root, registry, report)
    check_c3_invalid(root, registry, report)
    check_c4_error_codes(root, registry, report)
    check_c5_session_states(root, report)
    check_c6_placeholder_hygiene(root, report)
    check_c7_policy_files(root, report)

    print()
    if report.failures:
        print(f"结果：失败（{len(report.failures)} 处）")
        return 1
    print("结果：全部通过")
    return 0


if __name__ == "__main__":
    sys.exit(main())
