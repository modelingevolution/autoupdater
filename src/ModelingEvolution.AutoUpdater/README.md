# Docker Autoupdate IoT Architecture

This project is a simple example of how to create a docker container that will automatically update itself when a new version is available. This is useful for IoT devices that are not easily accessible or for devices that are deployed in the field.

The project is split into two parts:

1. The `EventPi.AutoUpdate` project which is a .NET Core console application that will check for updates and restart the container if a new version is available.
2. The `EventPi.AutoUpdate.Host` project which is a docker container that will run the `EventPi.AutoUpdate` application.

There are 3 main update processes supported:

1. Updating the AutoUpdate application itself,
2. Updating external contianers through docker-compose,
3. Updating the system.

## Updating the AutoUpdate application

The `EventPi.AutoUpdate.Host` application will check for updates on a regular interval. If a new version is available, the application will download the new version and create a new container.

1. ```docker pull``` will be performed to get the latest version of the container.
2. Old version of the updater will hand-over the control to the new version of the updater, by invoking a REST-API call.
3. Communication between old and new version of the updater will be done through external docker-network, to which containers will be attached to.
4. Once the new version successfully receives the control, it will stop the old version of the updater and remove the container.

## Updating external containers through docker-compose

The `EventPi.AutoUpdate.Host` application can also update external containers through docker-compose. The `EventPi.AutoUpdate.Host` application will check for updates on a regular interval. If a new version is available, the application will download the new version and restart the external container:

1. 'docker-compose.yml' files are stored in a git repository on a branch dedicated for IoT client.
2. The `EventPi.AutoUpdate.Host` application will clone the repository and checkout the branch.
3. The `EventPi.AutoUpdate.Host` application will perform a ```git pull``` to get the latest version of the repository. If there are changes next steps will be perfomed.
4. The `EventPi.AutoUpdate.Host` application will perform a ```docker-compose pull``` to get the latest version of the container.
5. The `EventPi.AutoUpdate.Host` application will perform a ```docker-compose up -d``` to restart the container at the right moment defined in our policies. (for instance only on RPI start)

## Updating the system: docker-host

The 'EventPi.AutoUpdate.Host' container can connect to the host through SSH. In this way administrative tasks could be performed:

1. Updating all the packages on the host including docker and pwsh.
2. Updating the kernel, etc.

## Error handling

The AutoUpdate process can handle errors in the following ways:

1. If the AutoUpdate application fails to start, the container will be stopped and the error will be logged, by the old container. The old container can have a retry policy and also notify vendor about the problem.
2. If the AutoUpdate application fails to receive the control from the old version, the old version will continue to run and the error will be logged. The old container can have a retry policy and also notify vendor about the problem. The new container will be stopped and deleted.

The external container update process can handle errors in the following ways:

1. If the stack fails to start, the error will be logged and the previous version of the docker-compose files will be used, by reverting the version in git repository.
2. If previous version also failes, the error will be logged and factory-reset script will be performed.


### The 'no space left' issue

1. The AutoUpdate application won't be able to download the new versions or log errors. Very few administrative tasks could be perfomed. We cannot let this happen.
2. The reason for that might be that the video-storage is full. So when ffmpeg saves the stream, we need to contantly monitor the space. If space is less than 15% the recording process should be stopped.
3. Another reason might be that docker images are taking too much space. The space could be reclaimed by executing ```docker system prune``` command by the AutoUpdater.
3. The lest reason might be that OS simply get's bigger. In this case we need to patiently wait for space to be reclaimed. Maybe a notification to the user, that videos need to be deleted?

## Resizing RPI partition

### If you have a hard disk, not a usb stick
1. 1. Execute on windows:
```cmd
GET-CimInstance -query "SELECT * from Win32_DiskDrive"
```
2. Find the disk.
3. Execute on windows:
```cmd
wsl --mount <DiskPath>
```
4. Execute on wsl linux:
```bash
sudo fdisk -l # OR
sudo 
sudo resize2fs /dev/<Partition>
```

### If you have a usb stick

Boot Ubuntu Desktop from USB stick.
Execute
```bash
# Check the disks and partitions
lsblk
resize2fs /dev/<YOUR_ID>
```

Alternatively:
https://github.com/jovton/USB-Storage-on-WSL2
