"""Compare live Swarm graphs with a leftover Qwen toggle after switching presets.

Usage: python check_preset_routing.py PRESETS.json --url http://127.0.0.1:7861
This builds graphs only; it does not queue generation or download models.
Presets needing unavailable models or media inputs are reported as skipped.
"""
import argparse
import json
from pathlib import Path
import urllib.error
import urllib.request


def check(url, presets):
    session = {}

    def api(name, parameters):
        request = urllib.request.Request(url.rstrip('/') + '/API/' + name,
                                        json.dumps(session | parameters).encode(),
                                        {'Content-Type': 'application/json'})
        try:
            with urllib.request.urlopen(request, timeout=120) as response:
                return json.load(response)
        except urllib.error.HTTPError as error:
            return json.loads(error.read())

    session['session_id'] = api('GetNewSession', {})['session_id']
    passed, skipped = [], []
    for title, preset in presets.items():
        if preset['param_map'].get('qwenunifiedenabled') == 'true':
            continue
        params = preset['param_map'] | {'prompt': 'A red teapot on a white table.', 'seed': '1', 'images': 1}
        baseline = api('ComfyGetGeneratedWorkflow', params | {'qwenunifiedenabled': 'false'})
        leftover = api('ComfyGetGeneratedWorkflow', params | {'qwenunifiedenabled': 'true'})
        if 'workflow' not in baseline:
            assert 'workflow' not in leftover, (title, 'Qwen intercepted an unsupported legacy request')
            skipped.append({'preset': title, 'reason': baseline.get('error', str(baseline))})
            continue
        assert 'workflow' in leftover, (title, leftover)
        original = json.loads(baseline['workflow'])
        switched = json.loads(leftover['workflow'])
        assert original == switched, (title, 'Leftover Qwen toggle changed the legacy graph')
        assert not any(n['class_type'].startswith('SEQwenImage21') for n in switched.values()), title
        passed.append(title)
    assert passed, 'No legacy preset could be checked on this backend'
    return {'passed': passed, 'skipped': skipped}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('presets', type=Path)
    parser.add_argument('--url', default='http://127.0.0.1:7861')
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    result = check(args.url, json.loads(args.presets.read_text(encoding='utf-8')))
    if args.output:
        args.output.write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(f"Unchanged graphs: {len(result['passed'])}; unavailable presets: {len(result['skipped'])}")
