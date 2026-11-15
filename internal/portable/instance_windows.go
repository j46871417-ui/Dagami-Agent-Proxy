//go:build windows

package portable

import "context"

type Instance struct{}

func StartInstance(*Controller) (*Instance, error)                { return &Instance{}, nil }
func ForwardLaunch(context.Context, string, string) (bool, error) { return false, nil }
func (*Instance) Close()                                          {}

func ForwardRecovery(context.Context, string) (bool, RecoveryReport, error) {
	return false, RecoveryReport{}, nil
}

func lockInstance(string) (*Instance, error) { return &Instance{}, nil }
