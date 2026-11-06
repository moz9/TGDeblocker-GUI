"""Offline regression checks on the disposable TGDeblocker_API36 emulator only."""
import argparse
import pathlib
import re
import subprocess
import time
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser()
parser.add_argument('--adb', default='adb')
parser.add_argument('--reset-demo-data', action='store_true', required=True)
parser.add_argument('--output', type=pathlib.Path, default=pathlib.Path('artifacts/android-apk-check'))
args = parser.parse_args()
adb = [args.adb, '-s', 'emulator-5560']
package = 'app.tgdeblocker.mobile'


def call(*command):
    return subprocess.check_output(adb + list(command), stderr=subprocess.STDOUT, timeout=35)


if call('emu', 'avd', 'name').decode().splitlines()[0].strip() != 'TGDeblocker_API36':
    raise RuntimeError('Refusing to modify anything except the dedicated test AVD.')
if call('shell', 'getprop', 'sys.boot_completed').strip() != b'1':
    raise RuntimeError('The test emulator has not booted.')
args.output.mkdir(parents=True, exist_ok=True)
component = call('shell', 'cmd', 'package', 'resolve-activity', '--brief', package).decode().strip().splitlines()[-1]
if not component.startswith(package + '/'):
    raise RuntimeError('Application launch activity not found.')


def nodes():
    try:
        call('shell', 'pidof', package)
    except subprocess.CalledProcessError as error:
        (args.output / 'crash.log').write_bytes(call('logcat', '-d', '-b', 'crash'))
        raise RuntimeError('The application stopped; see crash.log.') from error
    call('shell', 'uiautomator', 'dump', '/sdcard/tgdeblocker-test.xml')
    return list(ET.fromstring(call('shell', 'cat', '/sdcard/tgdeblocker-test.xml')).iter('node'))


def wait(text, attribute='text'):
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        found = [n for n in nodes() if n.attrib.get(attribute) == text]
        if found:
            return found
        time.sleep(.3)
    raise RuntimeError(f'Expected visible control: {text}')


def tap(text, attribute='text', last=False):
    node = wait(text, attribute)[-1 if last else 0]
    left, top, right, bottom = map(int, re.findall(r'\d+', node.attrib['bounds']))
    call('shell', 'input', 'tap', str((left + right) // 2), str((top + bottom) // 2))


def screenshot(name):
    (args.output / (name + '.png')).write_bytes(call('exec-out', 'screencap', '-p'))


def background_and_return():
    previous_pid = call('shell', 'pidof', package).strip()
    call('shell', 'input', 'keyevent', 'KEYCODE_HOME')
    time.sleep(2)
    call('shell', 'am', 'start', '-W', '-n', component)
    if call('shell', 'pidof', package).strip() != previous_pid:
        raise RuntimeError('The process restarted; this is not a transient background test.')


call('shell', 'wm', 'size', 'reset')
call('shell', 'wm', 'density', 'reset')
call('shell', 'pm', 'clear', package)
call('logcat', '-c')
call('shell', 'am', 'start', '-W', '-n', component)
wait('Try without signing in')
screenshot('home')
tap('Phone with country code', 'hint')
call('shell', 'input', 'text', '+12025550123')
call('shell', 'input', 'keyevent', 'BACK')
background_and_return()
wait('+12025550123')
print('PASS phone input survives transient background', flush=True)

tap('Try without signing in')
wait('Review devices')
tap('Review devices')
wait('Pixel Demo')
tap('End all other sessions')
wait('End all other sessions?')
tap('Cancel')
wait('Pixel Demo')
print('PASS demo and cancellation', flush=True)

tap('Security', 'content-desc')
tap('Set a password')
tap('New password', 'hint')
call('shell', 'input', 'text', 'DemoOnly123')
call('shell', 'input', 'keyevent', 'TAB')
call('shell', 'input', 'text', 'DemoOnly123')
call('shell', 'input', 'keyevent', 'BACK')
background_and_return()
wait('Repeat password', 'hint')
tap('Save')
wait('Save this password?')
tap('Save', last=True)
wait('Two-step verification is enabled')
print('PASS demo password form and service survive background', flush=True)

tap('Settings', 'content-desc')
tap('System')
tap('Dark')
wait('Leave demo')
screenshot('dark')
tap('Dark')
tap('Light')
wait('Leave demo')
screenshot('light')
tap('Light')
tap('System')
tap('Automatic')
tap('Русский')
wait('Непрозрачные поверхности')
background_and_return()
wait('Непрозрачные поверхности')
wait('Выйти из демонстрации')
screenshot('russian-resume')
print('PASS themes, language and current page retained', flush=True)

pid = call('shell', 'pidof', package).decode().strip()
log = call('logcat', '-d', '--pid=' + pid).decode('utf-8', 'replace')
(args.output / 'process.log').write_text(log, encoding='utf-8')
if re.search(r'FATAL EXCEPTION|F monodroid|Fatal signal|Unhandled Exception', log):
    raise RuntimeError('Crash marker found in application log.')
print('PASS no crash markers in process log', flush=True)
