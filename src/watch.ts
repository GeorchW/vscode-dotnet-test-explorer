import * as path from "path";
import * as vscode from "vscode";
import { AppInsightsClient } from "./appInsightsClient";
import { Executor } from "./executor";
import { Logger } from "./logger";
import { TestCommands } from "./testCommands";
import { TestDirectories } from "./testDirectories";
import { Utility } from "./utility";
import { ChildProcess } from "child_process";
import { ITestResult } from "./testResult";
import { TestResultsListener, IResultMessage } from "./testResultsListener";

export class Watch {
    private processes = new Map<string, ChildProcess>();

    constructor(private testDirectories: TestDirectories) { }

    public async startWatch(
        onStart: () => void,
        onEnd: () => void,
        onResult: (results: ITestResult[]) => void) {
        for (const testDirectory of this.testDirectories.getTestDirectories()) {
            Logger.Log("Starting watch for " + testDirectory);

            const existingProcess = this.processes.get(testDirectory);
            if (existingProcess !== undefined && existingProcess.exitCode === null) {
                Logger.Log(`It seems like a process for ${testDirectory} is already running - it will be terminated.`);
                await Executor.terminate(existingProcess);
            }

            AppInsightsClient.sendEvent("runWatchCommand");

            const server = await TestResultsListener.create();
            server.onMessages((messages) => {
                let started = false;
                let ended = false;
                const results: IResultMessage[] = [];
                for (const message of messages) {
                    if (message.type === "result") {
                        results.push(message);
                    }
                    else if (message.type === "testRunStarted") {
                        started = true;
                    }
                    else if (message.type === "testRunComplete") {
                        ended = true
                    }
                }
                if (started) onStart();
                if (results) onResult(results);
                if (ended) onEnd();
            });

            const command = `dotnet watch test ${Utility.additionalArgumentsOption} `
                + `--verbosity:quiet `
                + `--test-adapter-path "${Utility.loggerPath}" `
                + `--logger "VsCodeLogger;port=${server.port}" `;
            const watcher = Executor.spawn(command, testDirectory);
            watcher.on("exit", () => server.dispose());
            this.processes.set(testDirectory, watcher);
        }
    }

    public async stopWatch() {
        const processes = [...this.processes.values()];
        this.processes.clear();
        await Promise.all(processes.map(p => Executor.terminate(p)));
    }
}
