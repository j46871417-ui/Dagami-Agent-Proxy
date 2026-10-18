"""Separate a frozen app's runtime from system tools and independent workers."""
import os
import sys


def external_environment(overrides=None):
    env=dict(os.environ)
    if getattr(sys,'frozen',False):
        if sys.platform.startswith('linux'):
            original=env.get('LD_LIBRARY_PATH_ORIG')
            if original is None:env.pop('LD_LIBRARY_PATH',None)
            else:env['LD_LIBRARY_PATH']=original
            env.pop('LD_LIBRARY_PATH_ORIG',None)
        runtime=getattr(sys,'_MEIPASS',None)
        if runtime:
            for key in ('PATH','DYLD_LIBRARY_PATH','DYLD_FALLBACK_LIBRARY_PATH'):
                if key in env:
                    env[key]=os.pathsep.join(value for value in env[key].split(os.pathsep)
                        if not value or not (os.path.abspath(value)==os.path.abspath(runtime) or os.path.abspath(value).startswith(os.path.abspath(runtime)+os.sep)))
        env={key:value for key,value in env.items() if not key.startswith('_PYI_') and key!='PYINSTALLER_RESET_ENVIRONMENT'}
    if overrides:env.update(overrides)
    return env


def independent_environment():
    env=external_environment()
    env={key:value for key,value in env.items() if not key.startswith('_PYI_')}
    env['PYINSTALLER_RESET_ENVIRONMENT']='1'
    return env
