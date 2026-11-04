"""Comparison for typed .NET reserialization of independently checked worker JSON.

This does not compare geometry files, source identities, author versus reopen
receipts, or production tolerances. Those retain their own strict checks.
"""
import math


def same_native_numbers(expected, actual):
    if isinstance(expected, bool) or isinstance(actual, bool):
        return type(expected) is type(actual) and expected == actual
    if isinstance(expected, (int, float)) and isinstance(actual, (int, float)):
        if not math.isfinite(expected) or not math.isfinite(actual):
            return False
        if isinstance(expected, int) and isinstance(actual, int):
            return expected == actual
        return expected == actual or actual in (math.nextafter(expected, -math.inf), math.nextafter(expected, math.inf))
    if isinstance(expected, dict) and isinstance(actual, dict):
        return expected.keys() == actual.keys() and all(same_native_numbers(value, actual[key]) for key, value in expected.items())
    if isinstance(expected, list) and isinstance(actual, list):
        return len(expected) == len(actual) and all(same_native_numbers(left, right) for left, right in zip(expected, actual))
    return type(expected) is type(actual) and expected == actual
