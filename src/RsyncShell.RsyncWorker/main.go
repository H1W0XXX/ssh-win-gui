package main

import (
	"context"
	"fmt"
	"os"
)

const workerVersion = "0.1.0"

func main() {
	worker := NewWorker(os.Stdin, os.Stdout)
	if err := worker.Run(context.Background()); err != nil {
		fmt.Fprintln(os.Stderr, "rsync worker:", err)
		os.Exit(1)
	}
}
