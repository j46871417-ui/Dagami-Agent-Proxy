//go:build !windows

package portable

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"os"
	"path/filepath"
	"sync"
	"syscall"
	"time"
)

type Instance struct {
	listener net.Listener
	lock     *os.File
	path     string
	once     sync.Once
}
type instanceRequest struct {
	Action string `json:"action"`
	App    string `json:"app"`
}
type instanceReply struct {
	Error  string          `json:"error,omitempty"`
	Report *RecoveryReport `json:"report,omitempty"`
}

func instancePath(dir string) (string, error) {
	dir, e := StateDirectory(dir)
	if e != nil {
		return "", e
	}
	hash := sha256.Sum256([]byte(dir))
	root := filepath.Join("/tmp", fmt.Sprintf("dagami-agent-%d-%s", os.Getuid(), hex.EncodeToString(hash[:8])))
	if e = os.Mkdir(root, 0700); e != nil && !os.IsExist(e) {
		return "", e
	}
	fi, e := os.Lstat(root)
	if e != nil {
		return "", e
	}
	st, ok := fi.Sys().(*syscall.Stat_t)
	if !fi.IsDir() || fi.Mode().Perm()&0077 != 0 || !ok || int(st.Uid) != os.Getuid() {
		return "", errors.New("небезопасный каталог локального управления")
	}
	return filepath.Join(root, "control.sock"), nil
}
func StartInstance(c *Controller) (*Instance, error) {
	instance, e := lockInstance(c.dir)
	if e != nil {
		return nil, e
	}
	path, e := instancePath(c.dir)
	if e != nil {
		instance.Close()
		return nil, e
	}
	instance.path = path
	return instance.listen(c)
}

func lockInstance(dir string) (*Instance, error) {
	info, e := os.Lstat(dir)
	if e != nil {
		return nil, e
	}
	if !info.IsDir() {
		return nil, errors.New("каталог состояния должен быть обычной папкой")
	}
	lockPath := filepath.Join(dir, "instance.lock")
	if info, e := os.Lstat(lockPath); e == nil && !info.Mode().IsRegular() {
		return nil, errors.New("небезопасная блокировка экземпляра")
	}
	lock, e := os.OpenFile(lockPath, os.O_CREATE|os.O_RDWR, 0600)
	if e != nil {
		return nil, e
	}
	if e = syscall.Flock(int(lock.Fd()), syscall.LOCK_EX|syscall.LOCK_NB); e != nil {
		lock.Close()
		return nil, errors.New("Dagami уже работает; используйте открытое окно")
	}
	return &Instance{lock: lock}, nil
}
func (instance *Instance) listen(c *Controller) (*Instance, error) {
	path := instance.path
	if info, e := os.Lstat(path); e == nil {
		if info.Mode()&os.ModeSocket == 0 {
			instance.Close()
			return nil, errors.New("путь локального управления занят")
		}
		if e := os.Remove(path); e != nil {
			instance.Close()
			return nil, e
		}
	}
	listener, e := net.Listen("unix", path)
	if e != nil {
		instance.Close()
		return nil, e
	}
	instance.listener = listener
	if e = os.Chmod(path, 0600); e != nil {
		instance.Close()
		return nil, e
	}
	go func() {
		slots := make(chan struct{}, 8)
		for {
			conn, e := listener.Accept()
			if e != nil {
				return
			}
			select {
			case slots <- struct{}{}:
				go func() { defer func() { <-slots }(); serveInstance(conn, c) }()
			default:
				conn.Close()
			}
		}
	}()
	return instance, nil
}
func serveInstance(conn net.Conn, c *Controller) {
	defer conn.Close()
	conn.SetDeadline(time.Now().Add(130 * time.Second))
	decoder := json.NewDecoder(io.LimitReader(conn, 4096))
	decoder.DisallowUnknownFields()
	var req instanceRequest
	var e error
	reply := instanceReply{}
	if e = decoder.Decode(&req); e == nil {
		if req.Action == "recover" && req.App == "" {
			report := c.Recover(context.Background())
			reply.Report = &report
			if !report.Success() {
				e = errors.New("восстановление выполнено частично; журналы сохранены")
			}
			c.notifyRemoteLaunch(e)
		} else if req.Action == "launch" && safeAppID.MatchString(req.App) {
			e = c.Launch(req.App)
			c.notifyRemoteLaunch(e)
		} else {
			e = errors.New("неизвестная локальная команда")
		}
	}
	if e != nil {
		reply.Error = Redact(e.Error())
	}
	_ = json.NewEncoder(conn).Encode(reply)
}
func ForwardLaunch(ctx context.Context, dir, app string) (bool, error) {
	path, e := instancePath(dir)
	if e != nil {
		return false, e
	}
	dialer := net.Dialer{Timeout: time.Second}
	conn, e := dialer.DialContext(ctx, "unix", path)
	if e != nil {
		return false, nil
	}
	defer conn.Close()
	conn.SetDeadline(time.Now().Add(130 * time.Second))
	if e = json.NewEncoder(conn).Encode(instanceRequest{Action: "launch", App: app}); e != nil {
		return true, e
	}
	var reply instanceReply
	e = json.NewDecoder(io.LimitReader(conn, 4096)).Decode(&reply)
	if e != nil {
		return true, e
	}
	if reply.Error != "" {
		return true, errors.New(reply.Error)
	}
	return true, nil
}
func (i *Instance) Close() {
	if i == nil {
		return
	}
	i.once.Do(func() {
		if i.listener != nil {
			i.listener.Close()
			_ = os.Remove(i.path)
		}
		if i.lock != nil {
			syscall.Flock(int(i.lock.Fd()), syscall.LOCK_UN)
			i.lock.Close()
		}
	})
}

func ForwardRecovery(ctx context.Context, dir string) (bool, RecoveryReport, error) {
	path, e := instancePath(dir)
	if e != nil {
		return false, RecoveryReport{}, e
	}
	dialer := net.Dialer{Timeout: time.Second}
	conn, e := dialer.DialContext(ctx, "unix", path)
	if e != nil {
		return false, RecoveryReport{}, nil
	}
	defer conn.Close()
	conn.SetDeadline(time.Now().Add(130 * time.Second))
	if e = json.NewEncoder(conn).Encode(instanceRequest{Action: "recover"}); e != nil {
		return true, RecoveryReport{}, e
	}
	var reply instanceReply
	if e = json.NewDecoder(io.LimitReader(conn, 32768)).Decode(&reply); e != nil {
		return true, RecoveryReport{}, e
	}
	if reply.Report == nil {
		return true, RecoveryReport{}, errors.New("ответ восстановления не подтверждён")
	}
	if reply.Error != "" {
		e = errors.New(reply.Error)
	}
	return true, *reply.Report, e
}
