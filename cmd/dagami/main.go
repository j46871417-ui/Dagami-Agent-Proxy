package main

import (
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"

	"dagami.local/agentproxy/internal/buildinfo"
	"dagami.local/agentproxy/internal/desktop"
	"dagami.local/agentproxy/internal/portable"
)

func main() {
	if e := run(); e != nil {
		var child *exec.ExitError
		if errors.As(e, &child) && child.ExitCode() > 0 {
			os.Exit(child.ExitCode())
		}
		fmt.Fprintln(os.Stderr, portable.Redact(e.Error()))
		os.Exit(1)
	}
}
func run() error {
	flags := flag.NewFlagSet("DagamiAgentProxy", flag.ContinueOnError)
	dir := flags.String("state-dir", "", "каталог настроек")
	preview := flags.Bool("preview", false, "демо без записи настроек и запуска приложений")
	page := flags.String("page", "apps", "открыть раздел apps, connection, dns, journal или recovery")
	launch := flags.String("launch", "", "запустить зарегистрированное приложение через существующий экземпляр")
	recoverNetwork := flags.Bool("recover", false, "восстановить собственные настройки без GUI и сервера")
	probe := flags.Bool("doctor", false, "состояние, без изменения сети")
	version := flags.Bool("version", false, "версия сборки")
	if e := flags.Parse(os.Args[1:]); e != nil {
		return e
	}
	if flags.NArg() != 0 && flags.Arg(0) != "run" && flags.Arg(0) != "inspect-binary" {
		return fmt.Errorf("неизвестная команда: %s", flags.Arg(0))
	}
	if *version {
		fmt.Fprintln(os.Stdout, buildinfo.ReleaseTag)
		return nil
	}
	if *recoverNetwork {
		if *preview {
			return errors.New("восстановление отключено в превью")
		}
		report, err := portable.StandaloneRecovery(context.Background(), *dir)
		_ = json.NewEncoder(os.Stdout).Encode(report)
		return err
	}
	if flags.NArg() > 0 && flags.Arg(0) == "inspect-binary" {
		if flags.NArg() != 2 {
			return errors.New("inspect-binary PATH")
		}
		value, err := portable.InspectBinary(flags.Arg(1))
		if err != nil {
			return err
		}
		return json.NewEncoder(os.Stdout).Encode(value)
	}
	if *launch != "" && !*preview && !*probe {
		if forwarded, err := portable.ForwardLaunch(context.Background(), *dir, *launch); forwarded || err != nil {
			return err
		}
	}
	if strings.EqualFold(filepath.Base(os.Args[0]), "DagamiRecovery") {
		if !*preview && !*probe {
			return desktop.RunRecovery(*dir)
		}
	}
	c, e := portable.NewController(*dir, *preview)
	if e != nil {
		if !*probe && *launch == "" && flags.NArg() == 0 {
			return desktop.RunRecovery(*dir)
		}
		return e
	}
	defer c.Close()
	if *probe {
		return json.NewEncoder(os.Stdout).Encode(c.Snapshot())
	}
	if flags.NArg() > 0 && flags.Arg(0) == "run" {
		if flags.NArg() < 2 {
			return errors.New("run APP_ID [-- ARGS]")
		}
		args := flags.Args()[2:]
		if len(args) > 0 && args[0] == "--" {
			args = args[1:]
		}
		return c.RunAttached(flags.Arg(1), args, os.Stdin, os.Stdout, os.Stderr)
	}
	if *preview {
		return desktop.Run(c, *page)
	}
	instance, err := portable.StartInstance(c)
	if err != nil {
		return err
	}
	defer instance.Close()
	return desktop.RunLaunch(c, *page, *launch)
}
