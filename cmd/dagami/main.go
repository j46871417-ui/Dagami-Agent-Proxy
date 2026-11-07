package main

import (
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"os"
	"os/exec"

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
	if len(os.Args) >= 3 && os.Args[1] == "run" {
		c, e := portable.NewController("", false)
		if e != nil {
			return e
		}
		defer c.Close()
		args := os.Args[3:]
		if len(args) > 0 && args[0] == "--" {
			args = args[1:]
		}
		return c.RunAttached(os.Args[2], args, os.Stdin, os.Stdout, os.Stderr)
	}
	if len(os.Args) == 3 && os.Args[1] == "inspect-binary" {
		r, e := portable.InspectBinary(os.Args[2])
		if e != nil {
			return e
		}
		return json.NewEncoder(os.Stdout).Encode(r)
	}
	flags := flag.NewFlagSet("DagamiAgentProxy", flag.ContinueOnError)
	dir := flags.String("state-dir", "", "каталог настроек")
	preview := flags.Bool("preview", false, "демо без записи настроек и запуска приложений")
	page := flags.String("page", "apps", "открыть раздел apps, connection, dns или journal")
	probe := flags.Bool("doctor", false, "состояние, без изменения сети")
	version := flags.Bool("version", false, "версия сборки")
	if e := flags.Parse(os.Args[1:]); e != nil {
		return e
	}
	if flags.NArg() != 0 {
		return fmt.Errorf("неизвестная команда: %s", flags.Arg(0))
	}
	if *version {
		fmt.Fprintln(os.Stdout, buildinfo.ReleaseTag)
		return nil
	}
	c, e := portable.NewController(*dir, *preview)
	if e != nil {
		return e
	}
	defer c.Close()
	if *probe {
		return json.NewEncoder(os.Stdout).Encode(c.Snapshot())
	}
	return desktop.Run(c, *page)
}
