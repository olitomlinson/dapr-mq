import { log } from "./logger.js";
import { createControlServer } from "./server.js";

const port = Number(process.env.CONTROL_PORT ?? "8080");

const server = createControlServer();
server.listen(port, () => {
  log("INFO", `control server listening on port ${port}`);
});
