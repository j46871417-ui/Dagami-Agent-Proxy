package main

import (
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"os"
	"os/exec"
	"os/signal"
	"syscall"

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
	headless := flags.Bool("no-browser", false, "только локальный интерфейс без открытия браузера")
	preview := flags.Bool("preview", false, "демо без записи настроек и запуска приложений")
	probe := flags.Bool("doctor", false, "состояние, без изменения сети")
	if e := flags.Parse(os.Args[1:]); e != nil {
		return e
	}
	if flags.NArg() != 0 {
		return fmt.Errorf("неизвестная команда: %s", flags.Arg(0))
	}
	c, e := portable.NewController(*dir, *preview)
	if e != nil {
		return e
	}
	defer c.Close()
	if *probe {
		return json.NewEncoder(os.Stdout).Encode(c.Snapshot())
	}
	s, e := portable.StartServer(c)
	if e != nil {
		return e
	}
	defer s.Close()
	if portable.RoutingOnly {
		fmt.Println("Dagami Agent Proxy · Routing")
	} else {
		fmt.Println("Dagami Agent Proxy · Go + Rust")
	}
	fmt.Println("Локальный интерфейс: " + s.URL + "/#" + s.Token)
	if !*headless {
		if e = s.Open(); e != nil {
			fmt.Fprintln(os.Stderr, "Откройте локальную ссылку выше в браузере.")
		}
	}
	signals := make(chan os.Signal, 1)
	signal.Notify(signals, os.Interrupt, syscall.SIGTERM)
	defer signal.Stop(signals)
	go func() { <-signals; s.Close() }()
	s.Wait()
	return nil
}
