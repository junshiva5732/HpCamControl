module.exports = {
  apps: [
    {
      name: "hpcamcontrol-relay",
      script: "index.js",
      env: {
        PORT: 3000
      },
      autorestart: true,
      watch: false
    }
  ]
};
